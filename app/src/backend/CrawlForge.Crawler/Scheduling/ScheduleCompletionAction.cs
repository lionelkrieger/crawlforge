using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Kinds of pre-authorized completion actions (FS-CRAWL-001 §6.5). Actions never run
/// without prior explicit configuration and only target user-authorized destinations:
/// a local OS notification, one of the user's configured export destinations, or a
/// notification endpoint the user configured themselves. No vendor or telemetry
/// destination is ever contacted (exclusion rows 6/7).
/// </summary>
public static class CompletionActionKinds
{
    /// <summary>Local OS notification surfaced through the shell.</summary>
    public const string LocalNotification = "local-notification";

    /// <summary>Export generated for the audit and delivered to a user-configured destination.</summary>
    public const string ExportDelivery = "export-delivery";

    /// <summary>Completion message posted to a user-configured notification endpoint.</summary>
    public const string NotificationEndpoint = "notification-endpoint";
}

/// <summary>
/// One pre-authorized completion action stored in
/// <c>schedule_definitions.completion_actions_json</c>.
/// </summary>
/// <param name="Kind">Action kind (see <see cref="CompletionActionKinds"/>).</param>
/// <param name="DestinationId">User-configured export destination (export delivery).</param>
/// <param name="Format">Export format (export delivery).</param>
/// <param name="Datasets">Optional dataset selection (export delivery).</param>
/// <param name="Endpoint">User-configured notification endpoint URL (notification endpoint).</param>
/// <param name="Title">Optional notification title.</param>
/// <param name="Message">Optional notification message.</param>
public sealed record ScheduleCompletionAction(
    string Kind,
    long? DestinationId = null,
    ExportFormat? Format = null,
    IReadOnlyList<string>? Datasets = null,
    string? Endpoint = null,
    string? Title = null,
    string? Message = null);

/// <summary>
/// Parses and serializes the completion-actions payload. Parsing validates every
/// action so misconfiguration surfaces at save time, never mid-run.
/// </summary>
public static class ScheduleCompletionActions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses a completion-actions JSON payload.</summary>
    /// <param name="json">Payload, or null/empty for no actions.</param>
    /// <returns>The validated actions (empty when no payload).</returns>
    /// <exception cref="FormatException">The payload is malformed or an action is invalid.</exception>
    public static IReadOnlyList<ScheduleCompletionAction> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        List<ScheduleCompletionAction>? actions;
        try
        {
            actions = JsonSerializer.Deserialize<List<ScheduleCompletionAction>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"The completion-actions payload is not valid JSON: {ex.Message}", ex);
        }

        if (actions is null)
        {
            return [];
        }

        foreach (var action in actions)
        {
            Validate(action);
        }

        return actions;
    }

    /// <summary>Serializes completion actions to their stored JSON shape.</summary>
    /// <param name="actions">Actions to serialize.</param>
    /// <returns>The JSON payload.</returns>
    public static string Serialize(IReadOnlyList<ScheduleCompletionAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        foreach (var action in actions)
        {
            Validate(action);
        }

        return JsonSerializer.Serialize(actions, JsonOptions);
    }

    private static void Validate(ScheduleCompletionAction action)
    {
        switch (action.Kind)
        {
            case CompletionActionKinds.LocalNotification:
                break;

            case CompletionActionKinds.ExportDelivery:
                if (action.DestinationId is null or <= 0)
                {
                    throw new FormatException("An export-delivery action requires a configured destination identifier.");
                }

                if (action.Format is null)
                {
                    throw new FormatException("An export-delivery action requires an export format.");
                }

                break;

            case CompletionActionKinds.NotificationEndpoint:
                if (string.IsNullOrWhiteSpace(action.Endpoint)
                    || !Uri.TryCreate(action.Endpoint, UriKind.Absolute, out var endpoint)
                    || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
                {
                    throw new FormatException("A notification-endpoint action requires an absolute http(s) endpoint configured by the user.");
                }

                break;

            default:
                throw new FormatException($"Unknown completion-action kind '{action.Kind}'.");
        }
    }
}
