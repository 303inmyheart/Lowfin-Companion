using System.Security.Cryptography;
using Lowfin.Companion.Discord;
using Xunit;
namespace Lowfin.Companion.Tests;
public sealed class DiscordTokenProtectionTests : IDisposable {
    private readonly string directory=Path.Combine(Path.GetTempPath(),"lowfin-secret-test-"+Guid.NewGuid().ToString("N"));
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
    [Fact] public void CiphertextSurvivesReopeningAndNormalizesUserId() {
        var user=Guid.NewGuid();var tokens=new DiscordTokenProtection(directory);string encrypted=tokens.Protect(user.ToString(),"fake-secret-token");
        Assert.StartsWith(DiscordTokenProtection.Prefix,encrypted);Assert.DoesNotContain("fake-secret-token",encrypted);
        Assert.Equal("fake-secret-token",new DiscordTokenProtection(directory).Read(user.ToString("N"),encrypted));
        Assert.Equal(encrypted,tokens.Protect(user.ToString(),encrypted));Assert.Equal("",tokens.Protect(user.ToString(),""));
        Assert.NotEmpty(Directory.GetFiles(directory,"*.xml"));
        if(!OperatingSystem.IsWindows()) {
            Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute,File.GetUnixFileMode(directory));
            foreach(var file in Directory.GetFiles(directory,"*.xml"))Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(file));
        }
    }
    [Fact] public void WrongUserMissingKeysAndTamperingCannotReturnPlaintext() {
        var tokens=new DiscordTokenProtection(Path.Combine(directory,"original"));string user=Guid.NewGuid().ToString(),encrypted=tokens.Protect(user,"fake-secret-token");
        Assert.Throws<CryptographicException>(()=>tokens.Read(Guid.NewGuid().ToString(),encrypted));
        Assert.Throws<CryptographicException>(()=>new DiscordTokenProtection(Path.Combine(directory,"different")).Read(user,encrypted));
        int n=DiscordTokenProtection.Prefix.Length+30;var tampered=encrypted[..n]+(encrypted[n]=='A'?'B':'A')+encrypted[(n+1)..];
        Assert.Throws<CryptographicException>(()=>tokens.Read(user,tampered));Assert.Equal(encrypted,tokens.Protect(user,encrypted));
    }
    [Fact] public void MigrationProtectsDisabledMappingsAndPreservesUnrelatedSettings() {
        var config=new PluginConfiguration {OmdbKey="unrelated-provider-fixture",DiscordRpc=new(){Enabled=false,Users=[new(){JellyfinUserId=Guid.NewGuid().ToString(),DiscordToken="fake-secret-token",Enabled=false}]}};
        var tokens=new DiscordTokenProtection(directory);Assert.True(tokens.ProtectConfiguration(config.DiscordRpc));Assert.False(tokens.ProtectConfiguration(config.DiscordRpc));
        Assert.Equal("unrelated-provider-fixture",config.OmdbKey);Assert.False(config.DiscordRpc.Enabled);Assert.False(config.DiscordRpc.Users[0].Enabled);
        using var output=new StringWriter();new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration)).Serialize(output,config);
        Assert.DoesNotContain("fake-secret-token",output.ToString());Assert.Contains(DiscordTokenProtection.Prefix,output.ToString());
    }
}
