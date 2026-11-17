using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>Minimal in-memory connection tracking opens, closes, and transactions.</summary>
internal sealed class FakeDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    public int OpenCount { get; private set; }

    public int CloseCount { get; private set; }

    public List<FakeDbTransaction> Transactions { get; } = [];

    public List<MetricSummaryRow> MetricRows { get; set; } = [];

    /// <summary>
    /// Raw audit_scores rows in the REAL catalog shape (DEFECT D10): nullable
    /// integer category plus the four contributing count columns — the reader
    /// composes the parity DTOs from these, exactly as on a live provider.
    /// </summary>
    public List<FakeScoreRawRow> ScoreRows { get; set; } = [];

    public string? LastCommandText { get; private set; }

    public List<string> CommandTexts { get; } = [];

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "8.0.36";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close()
    {
        _state = ConnectionState.Closed;
        CloseCount++;
    }

    public override void Open()
    {
        _state = ConnectionState.Open;
        OpenCount++;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var transaction = new FakeDbTransaction(this);
        Transactions.Add(transaction);
        return transaction;
    }

    protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);

    protected override DbProviderFactory DbProviderFactory => throw new NotSupportedException();

    internal void RecordCommand(string commandText)
    {
        LastCommandText = commandText;
        CommandTexts.Add(commandText);
    }
}

/// <summary>
/// One raw audit_scores row in the locked catalog shape (DEFECT D10): the
/// nullable AuditCategory ordinal and the four contributing count columns the
/// score recorder persists.
/// </summary>
internal sealed record FakeScoreRawRow(int? Category, double Score, int ErrorCount, int WarningCount, int NoticeCount, int PassCount);

/// <summary>Transaction stub counting commits and rollbacks.</summary>
internal sealed class FakeDbTransaction : DbTransaction
{
    public FakeDbTransaction(DbConnection connection)
    {
        DbConnection = connection;
    }

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection { get; }

    public override void Commit() => CommitCount++;

    public override void Rollback() => RollbackCount++;
}

/// <summary>Command stub serving canned report rows for the parity-read SQL.</summary>
internal sealed class FakeDbCommand : DbCommand
{
    private readonly FakeDbConnection _connection;
    private readonly FakeDbParameterCollection _parameters = new();

    public FakeDbCommand(FakeDbConnection connection)
    {
        _connection = connection;
        DbConnection = connection;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    public override int ExecuteNonQuery() => throw new NotSupportedException();

    public override object? ExecuteScalar() => throw new NotSupportedException();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        _connection.RecordCommand(CommandText);
        if (CommandText.Contains("metric_summaries", StringComparison.Ordinal))
        {
            object?[][] rows = _connection.MetricRows
                .Select(r => new object?[] { r.MetricKey, r.DimensionsJson, r.Value })
                .ToArray();
            return new FakeDbDataReader(rows);
        }

        if (CommandText.Contains("audit_scores", StringComparison.Ordinal))
        {
            object?[][] rows = _connection.ScoreRows
                .Select(r => new object?[] { r.Category, r.Score, r.ErrorCount, r.WarningCount, r.NoticeCount, r.PassCount })
                .ToArray();
            return new FakeDbDataReader(rows);
        }

        throw new NotSupportedException("Unexpected command text: " + CommandText);
    }
}

/// <summary>Parameter stub.</summary>
internal sealed class FakeDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override DataRowVersion SourceVersion { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

/// <summary>Parameter collection stub over a list.</summary>
internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot { get; } = new();

    public override bool IsFixedSize => false;

    public override bool IsReadOnly => false;

    public override bool IsSynchronized => false;

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (object value in values)
        {
            _parameters.Add((DbParameter)value);
        }
    }

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains((DbParameter)value);

    public override bool Contains(string value) => _parameters.Exists(p => string.Equals(p.ParameterName, value, StringComparison.Ordinal));

    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) => _parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _parameters.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _parameters.RemoveAll(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) => _parameters.First(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        int index = IndexOf(parameterName);
        if (index >= 0)
        {
            _parameters[index] = value;
        }
        else
        {
            _parameters.Add(value);
        }
    }
}

/// <summary>Reader stub over canned rows.</summary>
internal sealed class FakeDbDataReader : DbDataReader
{
    private readonly object?[][] _rows;
    private int _index = -1;

    public FakeDbDataReader(object?[][] rows)
    {
        _rows = rows;
    }

    public override int Depth => 0;

    public override int FieldCount => _rows.Length == 0 ? 0 : _rows[0].Length;

    public override bool HasRows => _rows.Length > 0;

    private bool _isClosed;

    public override bool IsClosed => _isClosed;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => _rows[_index][ordinal]!;

    public override object this[string name] => _rows[_index][GetOrdinal(name)]!;

    public override bool Read() => ++_index < _rows.Length;

    public override bool NextResult() => false;

    public override void Close() => _isClosed = true;

    public override object GetValue(int ordinal) => _rows[_index][ordinal]!;

    public override bool IsDBNull(int ordinal) => _rows[_index][ordinal] is null;

    public override string GetString(int ordinal) => (string)_rows[_index][ordinal]!;

    public override double GetDouble(int ordinal) => Convert.ToDouble(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override int GetInt32(int ordinal) => Convert.ToInt32(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override long GetInt64(int ordinal) => Convert.ToInt64(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override byte GetByte(int ordinal) => Convert.ToByte(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => Convert.ToChar(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override Type GetFieldType(int ordinal) => _rows[_index][ordinal]?.GetType() ?? typeof(object);

    public override float GetFloat(int ordinal) => Convert.ToSingle(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override Guid GetGuid(int ordinal) => (Guid)_rows[_index][ordinal]!;

    public override short GetInt16(int ordinal) => Convert.ToInt16(_rows[_index][ordinal], CultureInfo.InvariantCulture);

    public override string GetName(int ordinal) => ordinal.ToString(CultureInfo.InvariantCulture);

    public override int GetOrdinal(string name) => int.Parse(name, CultureInfo.InvariantCulture);

    public override int GetValues(object[] values)
    {
        object?[] row = _rows[_index];
        int count = Math.Min(values.Length, row.Length);
        Array.Copy(row, values, count);
        return count;
    }

    public override DataTable? GetSchemaTable() => null;

    public override IEnumerator GetEnumerator() => _rows.GetEnumerator();
}
