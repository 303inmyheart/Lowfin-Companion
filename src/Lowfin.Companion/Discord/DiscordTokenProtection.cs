using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Lowfin.Companion.Discord;
/// <summary>Authenticated encryption at rest, using a persistent, plugin-owned Data Protection key ring.</summary>
public sealed class DiscordTokenProtection(string keyDirectory) {
    public const string Prefix="lowfin-protected:v1:";
    private readonly Lazy<IDataProtectionProvider> provider=new(()=>Create(keyDirectory));
    public string KeyDirectory=>keyDirectory;
    public static bool IsProtected(string? value)=>value?.StartsWith(Prefix,StringComparison.Ordinal)==true;
    private static IDataProtectionProvider Create(string path) {
        Directory.CreateDirectory(path);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        return DataProtectionProvider.Create(new DirectoryInfo(path),options=>options.SetApplicationName("Lowfin.Companion.68abfd24-882d-4937-a104-44d55181b41e"));
    }
    private IDataProtector Protector(string user) {
        string scope=Guid.TryParse(user,out var id)?id.ToString("N"):user.Trim().ToLowerInvariant();
        return provider.Value.CreateProtector("Lowfin.Companion.Discord.Token.v1",scope);
    }
    public string Protect(string user,string value) {
        if(string.IsNullOrWhiteSpace(value)||IsProtected(value))return value;
        string result=Prefix+Protector(user).Protect(value.Trim());
        if(!OperatingSystem.IsWindows())foreach(string key in Directory.EnumerateFiles(keyDirectory,"*.xml"))File.SetUnixFileMode(key,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        return result;
    }
    public string Read(string user,string value)=>IsProtected(value)?Protector(user).Unprotect(value[Prefix.Length..]):value.Trim();
    public bool ProtectConfiguration(DiscordOptions options) {
        // Complete encryption first so a failure never partially replaces in-memory mappings.
        var updates=options.Users.Select(user=>(User:user,Value:Protect(user.JellyfinUserId,user.DiscordToken))).ToArray();bool changed=false;
        foreach(var update in updates)if(update.User.DiscordToken!=update.Value){update.User.DiscordToken=update.Value;changed=true;}
        return changed;
    }
}
/// <summary>Migrate old cleartext mappings before the playback listener starts; keep unrelated features usable on failure.</summary>
public sealed class DiscordTokenMigration(ILogger<DiscordTokenMigration> log) : IHostedService {
    public Task StartAsync(CancellationToken cancellation) {
        try {
            if(Plugin.Instance is {} plugin&&plugin.DiscordTokens.ProtectConfiguration(plugin.Configuration.DiscordRpc)) {
                plugin.UpdateConfiguration(plugin.Configuration);log.LogInformation("Discord RPC tokens migrated to encrypted storage.");
            }
        }catch(Exception error){log.LogError("Discord RPC token migration failed ({Kind}). Check key-directory permissions; the previous configuration may still contain plaintext tokens until migration succeeds.",error.GetType().Name);}
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellation)=>Task.CompletedTask;
}
