using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOps.Infrastructure.Persistence;

/// <summary>
/// Explicit, operator-run database steps used by the deployment pipeline (never run at application startup).
/// The deploying identity runs them with DDL rights; the application's runtime identity gets only what it uses.
/// </summary>
public static partial class DatabaseAdministration
{
    /// <summary>Applies pending EF Core migrations. Idempotent.</summary>
    public static Task MigrateAsync(FinOpsDbContext db, CancellationToken cancellationToken = default) =>
        db.Database.MigrateAsync(cancellationToken);

    /// <summary>
    /// Creates the database user for the app's managed identity if it doesn't exist, and grants least privilege.
    /// The user is created from the identity's client ID (<c>WITH SID = …, TYPE = E</c>) rather than
    /// <c>FROM EXTERNAL PROVIDER</c>, so the SQL server needs no Microsoft Graph or Directory Readers permission when a
    /// pipeline service principal runs this. Without a client ID, the user must already exist. The app never deletes rows and never changes the schema, so:
    /// <list type="bullet">
    /// <item><c>organization</c>, <c>identity</c>: read only (users are provisioned by an operator).</item>
    /// <item><c>finance</c>, <c>messaging</c>: read, insert, update; <c>finance</c> procedures: execute.</item>
    /// <item><c>audit</c>: read and insert only, so the runtime identity cannot alter or remove audit history.</item>
    /// </list>
    /// No DELETE, no DDL, no <c>db_owner</c>. Idempotent.
    /// </summary>
    public static async Task GrantRuntimeAccessAsync(FinOpsDbContext db, string principalName,
        Guid? entraClientId = null, CancellationToken cancellationToken = default)
    {
        if (!PrincipalName().IsMatch(principalName))
            throw new ArgumentException("The principal name must be 1-128 letters, digits, '-', '_' or '.'.", nameof(principalName));
        if (entraClientId == Guid.Empty)
            throw new ArgumentException("The client ID must not be empty.", nameof(entraClientId));

        await db.Database.ExecuteSqlRawAsync(GrantSql, [
            new SqlParameter("@principal", System.Data.SqlDbType.NVarChar, 128) { Value = principalName },
            new SqlParameter("@clientId", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)entraClientId ?? DBNull.Value },
        ], cancellationToken);
    }

    // The name is passed as a parameter and quoted with QUOTENAME inside SQL; it is never concatenated in C#.
    // EXEC() accepts only literals and variables, so each statement is built into @sql first.
    private const string GrantSql = """
        DECLARE @user sysname = @principal;
        DECLARE @q nvarchar(300) = QUOTENAME(@user);
        DECLARE @sql nvarchar(max);
        IF DATABASE_PRINCIPAL_ID(@user) IS NULL
        BEGIN
            IF @clientId IS NULL
                THROW 50010, 'The database user does not exist.', 1;
            -- Azure SQL: an Entra application user whose SID is the managed identity's client ID.
            SET @sql = N'CREATE USER ' + @q + N' WITH SID = '
                + CONVERT(nvarchar(40), CAST(@clientId AS varbinary(16)), 1) + N', TYPE = E;';
            EXEC (@sql);
        END
        SET @sql = N'GRANT SELECT ON SCHEMA::organization TO ' + @q + N';'
            + N'GRANT SELECT ON SCHEMA::[identity] TO ' + @q + N';'
            + N'GRANT SELECT, INSERT, UPDATE, EXECUTE ON SCHEMA::finance TO ' + @q + N';'
            + N'GRANT SELECT, INSERT, UPDATE ON SCHEMA::messaging TO ' + @q + N';'
            + N'GRANT SELECT, INSERT, EXECUTE ON SCHEMA::audit TO ' + @q + N';';
        EXEC (@sql);
        """;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex PrincipalName();
}
