namespace FinOps.Application.Identity;
public interface IUserAccessReader
{
    Task<CurrentUserDto?> FindByEntraObjectIdAsync(Guid entraObjectId, CancellationToken cancellationToken);
}
