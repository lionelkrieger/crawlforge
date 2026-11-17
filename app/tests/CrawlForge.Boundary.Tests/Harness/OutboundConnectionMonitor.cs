using System.Net;
using System.Runtime.InteropServices;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>One observed TCP connection attributable to the monitored process.</summary>
/// <param name="At">Observation time (UTC).</param>
/// <param name="RemoteAddress">Remote endpoint address.</param>
/// <param name="RemotePort">Remote endpoint port.</param>
/// <param name="LocalAddress">Local endpoint address.</param>
/// <param name="LocalPort">Local endpoint port.</param>
/// <param name="State">TCP state name.</param>
/// <param name="Phase">Harness phase tag (idle / first-launch / audit / ...).</param>
/// <param name="IsBaseline">Whether the connection pre-existed product activity (test-harness plumbing).</param>
public sealed record ConnectionObservation(
    DateTimeOffset At, string RemoteAddress, int RemotePort, string LocalAddress, int LocalPort,
    string State, string Phase, bool IsBaseline)
{
    /// <summary>Whether the remote endpoint is loopback (on-machine).</summary>
    public bool IsLoopback => IPAddress.TryParse(RemoteAddress, out var address) && IPAddress.IsLoopback(address);
}

/// <summary>
/// Process-level outbound connection monitor (criterion #3 evidence): polls the
/// OS TCP connection table (iphlpapi GetExtendedTcpTable, no elevation required)
/// and records every connection owned by the target process, classified by
/// remote endpoint. Non-loopback observations during a zero-outbound phase are
/// boundary violations; during an audit phase they must match the crawl target
/// or configured proxy only.
/// </summary>
public sealed class OutboundConnectionMonitor : IDisposable
{
    private readonly int _processId;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<ConnectionObservation> _observations = [];
    private readonly HashSet<(string Remote, int RemotePort, int LocalPort)> _baseline = [];
    private readonly object _gate = new();
    private string _phase = "unspecified";
    private bool _baselineCaptured;

    private OutboundConnectionMonitor(int processId, TimeSpan interval)
    {
        _processId = processId;
        _interval = interval;
        _loop = Task.Run(() => PollLoopAsync(_stop.Token));
    }

    /// <summary>All observations (loopback and non-loopback) in observation order.</summary>
    public IReadOnlyList<ConnectionObservation> Observations
    {
        get { lock (_gate) { return [.. _observations]; } }
    }

    /// <summary>Only non-loopback observations — the boundary-relevant subset.</summary>
    public IReadOnlyList<ConnectionObservation> NonLoopbackObservations =>
        Observations.Where(o => !o.IsLoopback).ToList();

    /// <summary>Starts monitoring the given process.</summary>
    /// <param name="processId">Owning process id (the in-process backend's test host).</param>
    /// <param name="interval">Poll interval (default 200ms).</param>
    /// <returns>The running monitor.</returns>
    public static OutboundConnectionMonitor Start(int processId, TimeSpan? interval = null) =>
        new(processId, interval ?? TimeSpan.FromMilliseconds(200));

    /// <summary>Tags subsequent observations with a phase name for the evidence log.</summary>
    /// <param name="phase">Phase label.</param>
    public void SetPhase(string phase)
    {
        lock (_gate) { _phase = phase; }
    }

    /// <summary>
    /// Reads every TCP listener owned by the process (criterion #22 sweep):
    /// local address and port of each listening socket.
    /// </summary>
    /// <param name="processId">Owning process id.</param>
    /// <returns>Listener endpoints.</returns>
    public static IReadOnlyList<(string Address, int Port)> ReadListeners(int processId) =>
        TcpTable.ReadOwnerPidRows()
            .Where(r => r.ProcessId == processId && r.State == "Listen")
            .Select(r => (r.LocalAddress, r.LocalPort))
            .Distinct()
            .OrderBy(e => e.LocalAddress, StringComparer.Ordinal)
            .ThenBy(e => e.LocalPort)
            .ToList();

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutdown race.
        }

        _stop.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var rows = TcpTable.ReadOwnerPidRows();
                lock (_gate)
                {
                    if (!_baselineCaptured)
                    {
                        // Connections already established before any product activity
                        // are test-harness plumbing (testhost <-> runner), not product
                        // traffic. They stay in the evidence, tagged as baseline.
                        foreach (var row in rows.Where(r => r.ProcessId == _processId && r.RemotePort != 0))
                        {
                            _baseline.Add((row.RemoteAddress, row.RemotePort, row.LocalPort));
                        }

                        _baselineCaptured = true;
                    }

                    foreach (var row in rows)
                    {
                        if (row.ProcessId != _processId)
                        {
                            continue;
                        }

                        // Listener rows (remote port 0) are not outbound.
                        if (row.RemotePort == 0)
                        {
                            continue;
                        }

                        var isBaseline = _baseline.Contains((row.RemoteAddress, row.RemotePort, row.LocalPort));
                        _observations.Add(new ConnectionObservation(
                            DateTimeOffset.UtcNow, row.RemoteAddress, row.RemotePort,
                            row.LocalAddress, row.LocalPort, row.State, _phase, isBaseline));
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                // Platform without iphlpapi: monitoring degrades to unavailable;
                // tests assert the monitor actually produced observations.
            }

            try
            {
                await Task.Delay(_interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Minimal iphlpapi TCP-table reader (TCP_TABLE_OWNER_PID_ALL, IPv4 + IPv6).
/// Reads connection ownership only — no identity stores, no machine identifiers.
/// </summary>
internal static class TcpTable
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;

    internal sealed record Row(int ProcessId, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State);

    public static IReadOnlyList<Row> ReadOwnerPidRows()
    {
        var rows = new List<Row>();
        ReadFamily(AfInet, rows);
        ReadFamily(AfInet6, rows);
        return rows;
    }

    private static void ReadFamily(int family, List<Row> rows)
    {
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
        if (size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
            if (result != 0)
            {
                return;
            }

            var count = Marshal.ReadInt32(buffer);
            var offset = 4;
            for (var i = 0; i < count; i++)
            {
                if (family == AfInet)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid.
                    var state = Marshal.ReadInt32(buffer, offset);
                    var localAddr = new IPAddress((uint)Marshal.ReadInt32(buffer, offset + 4));
                    var localPort = DecodePort(Marshal.ReadInt32(buffer, offset + 8));
                    var remoteAddr = new IPAddress((uint)Marshal.ReadInt32(buffer, offset + 12));
                    var remotePort = DecodePort(Marshal.ReadInt32(buffer, offset + 16));
                    var pid = Marshal.ReadInt32(buffer, offset + 20);
                    rows.Add(new Row(pid, localAddr.ToString(), localPort, remoteAddr.ToString(), remotePort, StateName(state)));
                    offset += 24;
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScopeId, localPort,
                    // remoteAddr[16], remoteScopeId, remotePort, state, pid (56 bytes).
                    var localBytes = new byte[16];
                    Marshal.Copy(buffer + offset, localBytes, 0, 16);
                    var localScope = (uint)Marshal.ReadInt32(buffer, offset + 16);
                    var localAddr = new IPAddress(localBytes, localScope);
                    var localPort = DecodePort(Marshal.ReadInt32(buffer, offset + 20));
                    var remoteBytes = new byte[16];
                    Marshal.Copy(buffer + offset + 24, remoteBytes, 0, 16);
                    var remoteScope = (uint)Marshal.ReadInt32(buffer, offset + 40);
                    var remoteAddr = new IPAddress(remoteBytes, remoteScope);
                    var remotePort = DecodePort(Marshal.ReadInt32(buffer, offset + 44));
                    var state = Marshal.ReadInt32(buffer, offset + 48);
                    var pid = Marshal.ReadInt32(buffer, offset + 52);
                    rows.Add(new Row(pid, localAddr.ToString(), localPort, remoteAddr.ToString(), remotePort, StateName(state)));
                    offset += 56;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int DecodePort(int networkOrderDword) =>
        ((networkOrderDword & 0xFF) << 8) | ((networkOrderDword >> 8) & 0xFF);

    private static string StateName(int state) => state switch
    {
        1 => "Closed",
        2 => "Listen",
        3 => "SynSent",
        4 => "SynReceived",
        5 => "Established",
        6 => "FinWait1",
        7 => "FinWait2",
        8 => "CloseWait",
        9 => "Closing",
        10 => "LastAck",
        11 => "TimeWait",
        12 => "DeleteTcb",
        _ => $"State{state}",
    };

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
