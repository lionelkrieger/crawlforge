using Microsoft.AspNetCore.Identity;

namespace Fixture.Nr03;

public sealed class AccountService
{
    public string AccountId { get; set; } = string.Empty;

    public Task SignInAsync(string userName, string password) => Task.CompletedTask;

    public void ResetPassword(string email) { }
}
