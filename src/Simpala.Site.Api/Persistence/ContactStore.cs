using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Simpala.Site.Api.Persistence;

public enum ContactState { Pending, Sending, Sent, Failed, Unknown }
public sealed record ContactRecord(string Company, string IdempotencyKey, string Fingerprint,
    string Registration, DateTimeOffset CreatedAt, ContactState State);
public sealed class IdempotencyConflictException : Exception { }
public sealed class ChallengeConsumedException : Exception { }

public sealed class ContactStore(string path)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    { DataSource = path, DefaultTimeout = 30, Pooling = false }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Requests (
                Company TEXT NOT NULL CHECK(Company IN ('financeira','consorcio')),
                IdempotencyKey TEXT NOT NULL,
                Fingerprint TEXT NOT NULL,
                Registration TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                State TEXT NOT NULL CHECK(State IN ('Pending','Sending','Sent','Failed','Unknown')),
                Simulation INTEGER NOT NULL DEFAULT 1 CHECK(Simulation = 1),
                PRIMARY KEY (Company, IdempotencyKey)
            );
            CREATE TABLE IF NOT EXISTS UsedChallenges (
                Nonce TEXT PRIMARY KEY,
                ExpiresAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Requests_CreatedAt ON Requests(CreatedAt);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
    }

    public ContactRecord? Find(string company, string key)
    {
        using var connection = Open();
        return Find(connection, null, company, key);
    }

    private static ContactRecord? Find(SqliteConnection connection, SqliteTransaction? transaction, string company, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Fingerprint,Registration,CreatedAt,State FROM Requests WHERE Company=$company AND IdempotencyKey=$key";
        command.Parameters.AddWithValue("$company", company);
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(company, key, reader.GetString(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            Enum.Parse<ContactState>(reader.GetString(3))) : null;
    }

    public ContactRecord CreateOrGet(string company, string key, string fingerprint, string nonce,
        DateTimeOffset challengeExpiry, DateTimeOffset now)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = Find(connection, transaction, company, key);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint) throw new IdempotencyConflictException();
            transaction.Commit();
            return existing;
        }
        using (var challenge = connection.CreateCommand())
        {
            challenge.Transaction = transaction;
            challenge.CommandText = "INSERT INTO UsedChallenges(Nonce,ExpiresAt) VALUES($nonce,$expiry) ON CONFLICT(Nonce) DO NOTHING";
            challenge.Parameters.AddWithValue("$nonce", nonce);
            challenge.Parameters.AddWithValue("$expiry", challengeExpiry.ToString("O"));
            if (challenge.ExecuteNonQuery() == 0) throw new ChallengeConsumedException();
        }
        var prefix = company == "financeira" ? "FIN" : "CON";
        var day = TimeZoneInfo.ConvertTime(now, Services.EmailComposer.SaoPaulo).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var registration = $"FORMS-{prefix}-{day}-{Guid.NewGuid():N}".ToUpperInvariant();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Requests(Company,IdempotencyKey,Fingerprint,Registration,CreatedAt,State)
            VALUES($company,$key,$hash,$registration,$created,'Pending')
            """;
        insert.Parameters.AddWithValue("$company", company);
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$hash", fingerprint);
        insert.Parameters.AddWithValue("$registration", registration);
        insert.Parameters.AddWithValue("$created", now.ToString("O"));
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new(company, key, fingerprint, registration, now, ContactState.Pending);
    }

    public int Purge(DateTimeOffset cutoff, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Requests WHERE CreatedAt < $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var removed = command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM UsedChallenges WHERE ExpiresAt < $now";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        return removed;
    }
}
