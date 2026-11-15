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

    /// <summary>
    /// Provisions a real Entra user, identified by their object ID (<c>oid</c>), never their email: creates or updates
    /// the profile, then adds the role and an open department membership if missing. The same logic as
    /// <c>infra/sql/provision-user.sql</c>, for a local database. Idempotent; unknown roles or departments are refused.
    /// </summary>
    public static async Task ProvisionUserAsync(FinOpsDbContext db, Guid entraObjectId, string displayName, string email,
        string role, string? department, CancellationToken cancellationToken = default)
    {
        if (entraObjectId == Guid.Empty) throw new ArgumentException("The object ID must not be empty.", nameof(entraObjectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        await db.Database.ExecuteSqlRawAsync(ProvisionSql, [
            new SqlParameter("@objectId", System.Data.SqlDbType.UniqueIdentifier) { Value = entraObjectId },
            new SqlParameter("@displayName", System.Data.SqlDbType.NVarChar, 200) { Value = displayName },
            new SqlParameter("@email", System.Data.SqlDbType.NVarChar, 256) { Value = email },
            new SqlParameter("@role", System.Data.SqlDbType.NVarChar, 50) { Value = role },
            new SqlParameter("@department", System.Data.SqlDbType.NVarChar, 100) { Value = (object?)department ?? DBNull.Value },
        ], cancellationToken);
    }

    private const string ProvisionSql = """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;
        DECLARE @RoleId uniqueidentifier = (SELECT Id FROM [identity].Roles WHERE Name = @role);
        IF @RoleId IS NULL THROW 50101, 'Unknown role. Use Employee, Manager, FinanceOfficer or Administrator.', 1;
        DECLARE @DepartmentId uniqueidentifier = (SELECT Id FROM organization.Departments WHERE Name = @department);
        IF @department IS NOT NULL AND @DepartmentId IS NULL THROW 50102, 'Unknown department name.', 1;

        DECLARE @UserId uniqueidentifier = (SELECT Id FROM [identity].UserProfiles WHERE EntraObjectId = @objectId);
        IF @UserId IS NULL
        BEGIN
            SET @UserId = NEWID();
            INSERT [identity].UserProfiles (Id, EntraObjectId, DisplayName, Email, IsActive)
            VALUES (@UserId, @objectId, @displayName, @email, 1);
        END
        ELSE
            UPDATE [identity].UserProfiles SET DisplayName = @displayName, Email = @email WHERE Id = @UserId;

        IF NOT EXISTS (SELECT 1 FROM [identity].UserRoles WHERE UserId = @UserId AND RoleId = @RoleId)
            INSERT [identity].UserRoles (UserId, RoleId) VALUES (@UserId, @RoleId);

        IF @DepartmentId IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM [identity].DepartmentMemberships
            WHERE UserId = @UserId AND DepartmentId = @DepartmentId AND EndsAtUtc IS NULL)
            INSERT [identity].DepartmentMemberships (UserId, DepartmentId, StartsAtUtc, EndsAtUtc)
            VALUES (@UserId, @DepartmentId, SYSUTCDATETIME(), NULL);
        COMMIT;
        """;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex PrincipalName();
}
