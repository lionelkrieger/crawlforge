using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrawlForge.Data.Repositories;

/// <summary>
/// Storage kinds used by the provider-agnostic column mapping. Both the embedded
/// and the external provider store these as INTEGER / REAL / TEXT values, so the
/// logical schema is identical on either provider (precedence rule 6).
/// </summary>
public enum ColumnStorageKind
{
    /// <summary>64-bit integer (also used for enums, flags, and booleans).</summary>
    Integer,

    /// <summary>Floating-point number.</summary>
    Real,

    /// <summary>Text (strings, UTC timestamps in round-trip format, JSON payloads).</summary>
    Text
}

/// <summary>
/// One mapped column of an entity: its physical name, the DTO property it binds to,
/// and how values convert between the CLR and the provider-neutral storage kinds.
/// </summary>
public sealed class ColumnMap
{
    /// <summary>Physical column name (snake_case).</summary>
    public required string ColumnName { get; init; }

    /// <summary>DTO property name (PascalCase).</summary>
    public required string PropertyName { get; init; }

    /// <summary>CLR property type.</summary>
    public required Type PropertyType { get; init; }

    /// <summary>Storage kind used by both providers.</summary>
    public required ColumnStorageKind StorageKind { get; init; }

    /// <summary>Whether the column holds a JSON-serialized complex payload.</summary>
    public bool IsJson { get; init; }

    /// <summary>Whether the column is the surrogate primary key.</summary>
    public bool IsKey { get; init; }

    /// <summary>Reads the property value from a DTO instance.</summary>
    public required Func<object, object?> GetValue { get; init; }

    /// <summary>Writes the property value onto a DTO instance.</summary>
    public required Action<object, object?> SetValue { get; init; }
}

/// <summary>
/// Provider-agnostic entity map: binds one DTO record type to one logical table.
/// Column names follow the snake_case convention of the schema catalog; enums and
/// flags store as INTEGER, booleans as INTEGER 0/1, UTC timestamps as round-trip
/// TEXT, and complex payloads as JSON TEXT. The same map drives reads, writes,
/// filtering, sorting, archiving, and retention on either provider.
/// </summary>
public sealed class EntityMap
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SnakeCaseBoundary = new("(?<!^)([A-Z])", RegexOptions.Compiled);

    /// <summary>Logical table name.</summary>
    public required string TableName { get; init; }

    /// <summary>All mapped columns, including the surrogate key.</summary>
    public required IReadOnlyList<ColumnMap> Columns { get; init; }

    /// <summary>The surrogate key column.</summary>
    public required ColumnMap KeyColumn { get; init; }

    /// <summary>Soft-delete marker column, when the entity supports soft delete.</summary>
    public ColumnMap? SoftDeleteColumn { get; init; }

    /// <summary>Direct audit reference column, when the entity is audit-scoped.</summary>
    public ColumnMap? AuditColumn { get; init; }

    /// <summary>Direct project reference column, when the entity is project-scoped.</summary>
    public ColumnMap? ProjectColumn { get; init; }

    /// <summary>
    /// Non-generic row materializer (used by the archive service, which iterates the
    /// catalog without compile-time knowledge of each DTO type).
    /// </summary>
    public Func<DbDataReader, object>? MaterializeObject { get; set; }

    /// <summary>Non-key columns in stable order (used by INSERT/UPDATE).</summary>
    public IReadOnlyList<ColumnMap> ValueColumns => Columns.Where(c => !c.IsKey).ToList();

    /// <summary>Converts a DTO property name to the physical snake_case column name.</summary>
    /// <param name="propertyName">PascalCase property name.</param>
    /// <returns>snake_case column name.</returns>
    public static string ToColumnName(string propertyName) =>
        SnakeCaseBoundary.Replace(propertyName, "_$1").ToLowerInvariant();

    /// <summary>Gets the cached untyped map for a DTO type (archive/catalog scenarios).</summary>
    /// <param name="dtoType">The DTO record type.</param>
    /// <returns>The shared entity map.</returns>
    public static EntityMap For(Type dtoType) =>
        NonGenericCache.GetOrAdd(dtoType, static t =>
        {
            var typed = typeof(EntityMap<>).MakeGenericType(t)
                .GetProperty(nameof(EntityMap<object>.Instance))!.GetValue(null)!;
            return ((EntityMap)typed.GetType().GetProperty(nameof(EntityMap<object>.Map))!.GetValue(typed)!);
        });

    private static readonly ConcurrentDictionary<Type, EntityMap> NonGenericCache = new();

    /// <summary>Converts a CLR value to its provider-neutral storage representation.</summary>
    /// <param name="column">The column mapping.</param>
    /// <param name="value">The CLR value (may be null).</param>
    /// <returns>The storage value, or <see cref="DBNull.Value"/> for null.</returns>
    public static object ToStorageValue(ColumnMap column, object? value)
    {
        if (value is null)
        {
            return DBNull.Value;
        }

        if (column.IsJson)
        {
            return JsonSerializer.Serialize(value, column.PropertyType, JsonOptions);
        }

        return column.StorageKind switch
        {
            ColumnStorageKind.Integer => ToInteger(value),
            ColumnStorageKind.Real => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            ColumnStorageKind.Text => value is DateTimeOffset dto
                ? dto.ToString("O", CultureInfo.InvariantCulture)
                : Convert.ToString(value, CultureInfo.InvariantCulture)!,
            _ => throw new InvalidOperationException($"Unsupported storage kind {column.StorageKind}.")
        };
    }

    /// <summary>Converts a storage value back to the CLR property type.</summary>
    /// <param name="column">The column mapping.</param>
    /// <param name="value">The storage value (may be <see cref="DBNull.Value"/>).</param>
    /// <returns>The CLR value, or null for DBNull on nullable/reference types.</returns>
    public static object? FromStorageValue(ColumnMap column, object? value)
    {
        if (value is null || value is DBNull)
        {
            return null;
        }

        var targetType = Nullable.GetUnderlyingType(column.PropertyType) ?? column.PropertyType;

        if (column.IsJson)
        {
            return JsonSerializer.Deserialize(Convert.ToString(value, CultureInfo.InvariantCulture)!, targetType, JsonOptions);
        }

        if (targetType.IsEnum)
        {
            return Enum.ToObject(targetType, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        if (targetType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        if (targetType == typeof(bool))
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
        }

        if (targetType == typeof(string))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Converts a string filter/sort value (from <c>ListQuery</c>) to a storage value
    /// for the given column. Only whitelisted mapped columns reach this method.
    /// </summary>
    /// <param name="column">The column mapping.</param>
    /// <param name="text">The textual filter value.</param>
    /// <returns>The provider-neutral storage value.</returns>
    public static object FilterValueToStorage(ColumnMap column, string text)
    {
        var targetType = Nullable.GetUnderlyingType(column.PropertyType) ?? column.PropertyType;

        if (column.IsJson)
        {
            return text;
        }

        if (targetType.IsEnum)
        {
            var parsed = Enum.TryParse(targetType, text, ignoreCase: true, out var enumValue)
                ? enumValue
                : Enum.Parse(targetType, text, ignoreCase: true);
            return Convert.ToInt64(parsed, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(bool))
        {
            return bool.Parse(text) ? 1L : 0L;
        }

        if (targetType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToString("O", CultureInfo.InvariantCulture);
        }

        return column.StorageKind switch
        {
            ColumnStorageKind.Integer => long.Parse(text, CultureInfo.InvariantCulture),
            ColumnStorageKind.Real => double.Parse(text, CultureInfo.InvariantCulture),
            _ => text
        };
    }

    private static long ToInteger(object value)
    {
        var type = value.GetType();
        if (type.IsEnum)
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        return value is bool b ? (b ? 1L : 0L) : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Typed entity map for one DTO record type. Builds the column layout from the
/// record's public properties and materializes rows through the primary constructor
/// plus init-only additive members.
/// </summary>
/// <typeparam name="TDto">The DTO record type.</typeparam>
public sealed class EntityMap<TDto> where TDto : class
{
    private static readonly ConcurrentDictionary<Type, EntityMap<TDto>> Cache = new();

    /// <summary>The underlying untyped map.</summary>
    public EntityMap Map { get; }

    private readonly ConstructorInfo _constructor;
    private readonly ParameterInfo[] _constructorParameters;

    private EntityMap(EntityMap map, ConstructorInfo constructor, ParameterInfo[] constructorParameters)
    {
        Map = map;
        _constructor = constructor;
        _constructorParameters = constructorParameters;
    }

    /// <summary>Gets the cached map for <typeparamref name="TDto"/>.</summary>
    /// <returns>The shared typed entity map.</returns>
    public static EntityMap<TDto> Instance => Cache.GetOrAdd(typeof(TDto), _ => Build());

    /// <summary>Materializes one DTO from a data reader positioned on a row.</summary>
    /// <param name="reader">The reader; column names must match the map.</param>
    /// <returns>The materialized DTO.</returns>
    public TDto Materialize(DbDataReader reader)
    {
        var args = new object?[_constructorParameters.Length];
        for (var i = 0; i < _constructorParameters.Length; i++)
        {
            var parameter = _constructorParameters[i];
            var column = Map.Columns.First(c => string.Equals(c.PropertyName, parameter.Name, StringComparison.OrdinalIgnoreCase));
            var raw = reader[column.ColumnName];
            args[i] = EntityMap.FromStorageValue(column, raw) ?? GetDefaultValue(parameter.ParameterType);
        }

        var entity = (TDto)_constructor.Invoke(args);

        foreach (var column in Map.Columns)
        {
            if (_constructorParameters.Any(p => string.Equals(p.Name, column.PropertyName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var raw = reader[column.ColumnName];
            column.SetValue(entity, EntityMap.FromStorageValue(column, raw));
        }

        return entity;
    }

    private static object? GetDefaultValue(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? Activator.CreateInstance(type) : null;

    private static EntityMap<TDto> Build()
    {
        var type = typeof(TDto);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToList();

        var columns = new List<ColumnMap>();
        foreach (var property in properties)
        {
            var (kind, isJson) = Classify(property.PropertyType);
            columns.Add(new ColumnMap
            {
                ColumnName = SchemaNames.ColumnName(type, property.Name),
                PropertyName = property.Name,
                PropertyType = property.PropertyType,
                StorageKind = kind,
                IsJson = isJson,
                IsKey = string.Equals(property.Name, "Id", StringComparison.Ordinal),
                GetValue = property.GetValue,
                SetValue = property.SetValue
            });
        }

        var key = columns.SingleOrDefault(c => c.IsKey)
            ?? throw new InvalidOperationException($"{type.Name} has no Id key property.");

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var map = new EntityMap
        {
            TableName = SchemaNames.TableName(type),
            Columns = columns,
            KeyColumn = key,
            SoftDeleteColumn = columns.SingleOrDefault(c => c.PropertyName == "IsDeleted"),
            AuditColumn = columns.SingleOrDefault(c => c.PropertyName == "AuditId"),
            ProjectColumn = columns.SingleOrDefault(c => c.PropertyName == "ProjectId")
        };

        var typed = new EntityMap<TDto>(map, constructor, constructor.GetParameters());
        map.MaterializeObject = typed.Materialize;
        return typed;
    }

    private static (ColumnStorageKind Kind, bool IsJson) Classify(Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (type.IsEnum || type == typeof(bool) || type == typeof(long) || type == typeof(int) ||
            type == typeof(short) || type == typeof(byte))
        {
            return (ColumnStorageKind.Integer, false);
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return (ColumnStorageKind.Real, false);
        }

        if (type == typeof(string) || type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            return (ColumnStorageKind.Text, false);
        }

        return (ColumnStorageKind.Text, true);
    }
}
