using System.Security.Cryptography;
using System.Text.Json;
using System.Security.Claims;
using AlShifa.Api.Features.Calls;
using AlShifa.Api.Features.Realtime;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace AlShifa.Tests;

/// <summary>Pure voice contract tests: no API fixture, database, migrations or operational writes.</summary>
public sealed class RecordedVoicePackTests
{
    [Theory]
    [InlineData("calls.manage", "dept:audio")]
    [InlineData("preexam.edit", "dept:triage")]
    [InlineData("consultation.edit", "dept:doctor")]
    [InlineData("calls.manage,consultation.edit", "dept:audio,dept:doctor")]
    [InlineData("", "")]
    public async Task Real_signal_membership_keeps_each_screen_in_its_existing_permission_scope(string permissions, string expected)
    {
        var recorder = new GroupRecorder();
        var hub = new EventsHub(new ConnectionTracker()) { Context = new Caller(permissions), Groups = recorder };
        await hub.OnConnectedAsync();
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries).Order(), recorder.Joined.Order());
        await hub.OnDisconnectedAsync(null);
    }

    private sealed class GroupRecorder : IGroupManager
    {
        public List<string> Joined { get; } = [];
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        { Joined.Add(groupName); return Task.CompletedTask; }
        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Caller(string permissions) : HubCallerContext
    {
        public override string ConnectionId => "isolated-recording-test";
        public override string? UserIdentifier => "1";
        public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity(permissions.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => new Claim("permission", p)), "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    [Theory]
    [InlineData(1,"vision",CallDesk.Vision,"initial_exam")]
    [InlineData(25,"refraction",CallDesk.Refraction,"refraction")]
    [InlineData(50,"pharmacy",CallDesk.Pharmacy,"pharmacy")]
    [InlineData(12,"room-03",CallDesk.Doctor,"doctor")]
    [InlineData(12,"room-19",CallDesk.Doctor,"doctor")]
    [InlineData(12,"refraction",CallDesk.Refraction,"refraction")]
    [InlineData(3,"pharmacy",CallDesk.Pharmacy,"pharmacy")]
    [InlineData(25,"room-07",CallDesk.Vision,"initial_exam")]
    [InlineData(1,"imaging",CallDesk.Imaging,"imaging")]
    [InlineData(50,"optical",CallDesk.Optical,"optical")]
    [InlineData(25,"reception",CallDesk.Reception,"reception")]
    public void Recorded_pair_uses_real_number_and_stored_destination(int number,string destination,CallDesk desk,string key)
    {
        var original = destination;
        Assert.Equal(new[] {$"{VoicePack.RecordedBase}numbers/number_{number:D3}.wav",$"{VoicePack.RecordedBase}destinations/destination_{key}.wav"},VoicePack.RecordedClips(number,destination,desk));
        Assert.Equal(original,destination);
        Assert.DoesNotContain(VoicePack.RecordedClips(number,destination,desk),path=>path.Contains("departments"));
    }

    [Theory]
    [InlineData(0,"doctor")]
    [InlineData(51,"pharmacy")]
    [InlineData(100,"doctor")]
    [InlineData(12,"lab")]
    [InlineData(12,"اتاق داکتر")]
    [InlineData(12,"unknown")]
    [InlineData(12,"room-03")]
    public void Unsupported_audio_is_explicitly_empty_and_never_substituted(int number,string destination) =>
        Assert.Empty(VoicePack.RecordedClips(number,destination));

    [Fact]
    public void Every_pack_destination_is_explicitly_supported_including_unrouted_cashier_and_surgery()
    {
        foreach(var key in VoicePack.RecordedDestinations)
            Assert.EndsWith($"destination_{key}.wav",VoicePack.RecordedClips(25,key)[1]);
        Assert.Equal(59,VoicePack.RecordedFiles().Count());
    }

    [Fact]
    public void Bundled_68_WAVs_match_manifest_hashes_and_48000_mono_16bit_format()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory!=null && !Directory.Exists(Path.Combine(directory.FullName,"frontend","public","audio",VoicePack.RecordedVersion)))directory=directory.Parent;
        Assert.NotNull(directory);
        var root=Path.Combine(directory.FullName,"frontend","public","audio",VoicePack.RecordedVersion);
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        var clips=manifest.RootElement.GetProperty("clips").EnumerateArray().ToList();
        Assert.Equal(68,clips.Count);
        foreach(var clip in clips)
        {
            var data=File.ReadAllBytes(Path.Combine(root,clip.GetProperty("wav").GetString()!));
            Assert.Equal(clip.GetProperty("sha256_wav").GetString(),Convert.ToHexStringLower(SHA256.HashData(data)));
            using var reader=new BinaryReader(new MemoryStream(data));
            Assert.Equal("RIFF",new string(reader.ReadChars(4)));reader.ReadUInt32();Assert.Equal("WAVE",new string(reader.ReadChars(4)));
            var found=false;
            while(reader.BaseStream.Position+8<=reader.BaseStream.Length)
            {
                var chunk=new string(reader.ReadChars(4));var length=reader.ReadUInt32();var next=reader.BaseStream.Position+length;
                if(chunk=="fmt ")
                {
                    Assert.Equal(1,reader.ReadUInt16());Assert.Equal(1,reader.ReadUInt16());Assert.Equal(48000,reader.ReadInt32());
                    reader.ReadInt32();reader.ReadUInt16();Assert.Equal(16,reader.ReadUInt16());found=true;break;
                }
                reader.BaseStream.Position=next+(length%2);
            }
            Assert.True(found);
        }
    }
}
