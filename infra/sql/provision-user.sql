/*
  Provision one real Microsoft Entra user in a deployed FinOps database.

  Run as the SQL server's Entra administrator: the Azure portal Query editor (sign in with Entra), or
  sqlcmd/Azure Data Studio. The app's own identity cannot do this; it has read-only access to the identity schema.

  1. Fill in the four values below. Use the user's Entra OBJECT ID (oid), never their email, to identify them.
  2. Run the whole script. It is idempotent: running it again updates the name/email and adds missing grants.

  Roles: Employee, Manager, FinanceOfficer, Administrator (see docs/security/identity-authorization.md).
  Departments: Demo Engineering, Demo Finance, Demo Operations; NULL for none (Administrator, FinanceOfficer).
*/
DECLARE @EntraObjectId uniqueidentifier = '00000000-0000-0000-0000-000000000000';
DECLARE @DisplayName   nvarchar(200)    = N'First Last';
DECLARE @Email         nvarchar(256)    = N'first.last@example.com';
DECLARE @Role          nvarchar(50)     = N'Employee';
DECLARE @Department    nvarchar(100)    = N'Demo Engineering';

SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF @EntraObjectId = '00000000-0000-0000-0000-000000000000'
    THROW 50100, 'Set @EntraObjectId to the user''s Entra object ID.', 1;

DECLARE @RoleId uniqueidentifier = (SELECT Id FROM [identity].Roles WHERE Name = @Role);
IF @RoleId IS NULL THROW 50101, 'Unknown role. Use Employee, Manager, FinanceOfficer or Administrator.', 1;

DECLARE @DepartmentId uniqueidentifier = (SELECT Id FROM organization.Departments WHERE Name = @Department);
IF @Department IS NOT NULL AND @DepartmentId IS NULL THROW 50102, 'Unknown department name.', 1;

DECLARE @UserId uniqueidentifier = (SELECT Id FROM [identity].UserProfiles WHERE EntraObjectId = @EntraObjectId);
IF @UserId IS NULL
BEGIN
    SET @UserId = NEWID();
    INSERT [identity].UserProfiles (Id, EntraObjectId, DisplayName, Email, IsActive)
    VALUES (@UserId, @EntraObjectId, @DisplayName, @Email, 1);
END
ELSE
    UPDATE [identity].UserProfiles SET DisplayName = @DisplayName, Email = @Email WHERE Id = @UserId;

IF NOT EXISTS (SELECT 1 FROM [identity].UserRoles WHERE UserId = @UserId AND RoleId = @RoleId)
    INSERT [identity].UserRoles (UserId, RoleId) VALUES (@UserId, @RoleId);

IF @DepartmentId IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM [identity].DepartmentMemberships
    WHERE UserId = @UserId AND DepartmentId = @DepartmentId AND EndsAtUtc IS NULL)
    INSERT [identity].DepartmentMemberships (UserId, DepartmentId, StartsAtUtc, EndsAtUtc)
    VALUES (@UserId, @DepartmentId, SYSUTCDATETIME(), NULL);

COMMIT;

SELECT u.DisplayName, u.Email, r.Name AS [Role], d.Name AS Department
FROM [identity].UserProfiles AS u
JOIN [identity].UserRoles AS ur ON ur.UserId = u.Id
JOIN [identity].Roles AS r ON r.Id = ur.RoleId
LEFT JOIN [identity].DepartmentMemberships AS m ON m.UserId = u.Id AND m.EndsAtUtc IS NULL
LEFT JOIN organization.Departments AS d ON d.Id = m.DepartmentId
WHERE u.Id = @UserId;
