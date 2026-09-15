using WinMux.Platform;
using WinMux.Platform.Win32.Windows;

namespace WinMux.Platform.Win32.Tests;

/// <summary>
/// Credential targets, which are the part that needs no Windows to test.
/// </summary>
public sealed class CredentialTargetTests
{
    [Fact]
    public void A_profile_target_is_namespaced()
    {
        // The store is shared with every other application on the machine; a bare id would collide.
        Assert.Equal("WinMux:profile:build-box", CredentialTarget.ForProfile("build-box"));
    }

    [Fact]
    public void A_host_target_distinguishes_scheme_host_and_user()
    {
        // The same machine over SMB and over SFTP is two passwords; two accounts on it are two more.
        var smb = CredentialTarget.ForHost("smb", "nas.example.com", "alice");
        var sftp = CredentialTarget.ForHost("sftp", "nas.example.com", "alice");
        var other = CredentialTarget.ForHost("smb", "nas.example.com", "bob");

        Assert.NotEqual(smb, sftp);
        Assert.NotEqual(smb, other);
    }

    [Fact]
    public void Host_targets_are_case_insensitive_in_the_parts_that_are()
    {
        // DNS names and schemes are case-insensitive; a user name is not.
        Assert.Equal(
            CredentialTarget.ForHost("SMB", "NAS.Example.COM", "alice"),
            CredentialTarget.ForHost("smb", "nas.example.com", "alice"));
    }

    [Fact]
    public void A_target_always_starts_with_the_prefix()
    {
        // So a person can find and delete WinMux's entries in Control Panel without guessing.
        Assert.StartsWith(CredentialTarget.Prefix, CredentialTarget.ForProfile("x"), StringComparison.Ordinal);
        Assert.StartsWith(CredentialTarget.Prefix, CredentialTarget.ForHost("smb", "h", ""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_target_needs_something_to_name(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => CredentialTarget.ForProfile(id!));
    }
}

/// <summary>
/// The Windows store itself.
///
/// These write to the real Credential Manager, because a credential store that has never stored a
/// credential is worth nothing and the marshalling is exactly where this would break. Every entry
/// is namespaced with a fresh GUID and deleted in a finally, so a failure leaves no mess a person
/// would have to find.
/// </summary>
public sealed class Win32CredentialStoreTests
{
    private static string FreshTarget() =>
        CredentialTarget.ForProfile("test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_store_is_available_on_windows()
    {
        Assert.True(new Win32CredentialStore().IsAvailable);
    }

    [Fact]
    public void A_credential_survives_being_saved_and_read()
    {
        var store = new Win32CredentialStore();
        var target = FreshTarget();

        try
        {
            Assert.True(store.TrySave(target, new StoredCredential("alice", "correct horse battery"), out var error));
            Assert.Null(error);

            var loaded = store.Load(target);

            Assert.NotNull(loaded);
            Assert.Equal("alice", loaded.User);
            Assert.Equal("correct horse battery", loaded.Secret);
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void A_secret_with_non_ascii_survives_intact()
    {
        // The blob is UTF-16 and the marshalling is by hand, so this is where a byte-count mistake
        // would show up as a mangled password nobody could explain.
        var store = new Win32CredentialStore();
        var target = FreshTarget();
        const string secret = "pässwörd 日本語 🔐";

        try
        {
            Assert.True(store.TrySave(target, new StoredCredential("user", secret), out _));
            Assert.Equal(secret, store.Load(target)!.Secret);
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void Saving_again_replaces_rather_than_duplicates()
    {
        var store = new Win32CredentialStore();
        var target = FreshTarget();

        try
        {
            store.TrySave(target, new StoredCredential("alice", "first"), out _);
            store.TrySave(target, new StoredCredential("alice", "second"), out _);

            Assert.Equal("second", store.Load(target)!.Secret);
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void Loading_something_that_was_never_saved_returns_nothing()
    {
        // Null, not an exception: "no saved password" is an ordinary state that means "ask".
        Assert.Null(new Win32CredentialStore().Load(FreshTarget()));
    }

    [Fact]
    public void Deleting_reports_whether_there_was_anything_to_delete()
    {
        var store = new Win32CredentialStore();
        var target = FreshTarget();

        Assert.False(store.Delete(target));

        store.TrySave(target, new StoredCredential("user", "secret"), out _);
        Assert.True(store.Delete(target));
        Assert.Null(store.Load(target));
    }

    [Fact]
    public void A_credential_with_no_user_is_allowed()
    {
        // A token or a passphrase has no account attached to it.
        var store = new Win32CredentialStore();
        var target = FreshTarget();

        try
        {
            Assert.True(store.TrySave(target, new StoredCredential("", "token-only"), out _));

            var loaded = store.Load(target);
            Assert.Equal(string.Empty, loaded!.User);
            Assert.Equal("token-only", loaded.Secret);
        }
        finally
        {
            store.Delete(target);
        }
    }

    [Fact]
    public void A_secret_longer_than_windows_allows_is_refused_with_a_reason()
    {
        // Refused before the call rather than failing inside it, so the message says what is wrong
        // instead of reporting a Win32 error number.
        var store = new Win32CredentialStore();

        Assert.False(store.TrySave(FreshTarget(), new StoredCredential("u", new string('x', 4000)), out var error));
        Assert.NotNull(error);
        Assert.Contains("longer", error, StringComparison.OrdinalIgnoreCase);
    }
}
