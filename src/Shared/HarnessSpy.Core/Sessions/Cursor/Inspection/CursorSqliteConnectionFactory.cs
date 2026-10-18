using Microsoft.Data.Sqlite;

namespace HarnessSpy.Core.Sessions.Cursor.Inspection;

public sealed class CursorSqliteConnectionFactory
{
    private readonly int _commandTimeoutSeconds;

    public CursorSqliteConnectionFactory(int commandTimeoutSeconds = 2)
    {
        if (commandTimeoutSeconds is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commandTimeoutSeconds));
        }

        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public int CommandTimeoutSeconds => _commandTimeoutSeconds;

    public SqliteConnection Create(CursorDatabaseDescriptor database)
    {
        ArgumentNullException.ThrowIfNull(database);

        return Create(database.Path);
    }

    public SqliteConnection Create(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        SqliteConnectionStringBuilder connectionString = new()
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = _commandTimeoutSeconds
        };
        return new SqliteConnection(connectionString.ToString());
    }
}
