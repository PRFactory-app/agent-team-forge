using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentTeamForge.Host.Transport;
using AgentTeamForge.Host.Hosting;
using AgentTeamForge.Host.Features.Setup;
using AgentTeamForge.Business;
using Microsoft.Win32.SafeHandles;

namespace AgentTeamForge.Tests.Transport;

public sealed class WindowsPipeTests
{
    [Fact]
    public async Task Server_can_create_next_instance_while_previous_client_is_connected()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var name = "atf-test-" + Guid.NewGuid().ToString("N");
        using var first = WindowsPipe.CreateServer(name);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = first.WaitForConnectionAsync(timeout.Token);
        using var client = await WindowsPipe.ConnectAsync(name, timeout.Token);
        await accept;
        using var next = WindowsPipe.CreateServer(name);
    }

    [Fact]
    public async Task Server_is_user_owned_private_and_accepts_current_user()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var name = "atf-test-" + Guid.NewGuid().ToString("N");
        using var server = WindowsPipe.CreateServer(name);
        using var identity = WindowsIdentity.GetCurrent();
        var security = server.GetAccessControl();
        Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(timeout.Token);
        using var client = await WindowsPipe.ConnectAsync(name, timeout.Token);
        await accept;
        var data = new byte[1];
        var read = server.ReadExactlyAsync(data, timeout.Token);
        await client.WriteAsync(new byte[] { 42 }, timeout.Token);
        await read;
        Assert.Equal(42, data[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Same_user_connects_across_elevation_levels(bool elevatedServer)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { return; }
        using var token = CreateMediumToken(identity.AccessToken);
        var name = "atf-test-" + Guid.NewGuid().ToString("N");
        using var server = elevatedServer ? WindowsPipe.CreateServer(name)
            : WindowsIdentity.RunImpersonated(token, () =>
            {
                if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
                return WindowsPipe.CreateServer(name);
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accept = server.WaitForConnectionAsync(timeout.Token);
        var clientTask = elevatedServer ? WindowsIdentity.RunImpersonatedAsync(token, ConnectAndWriteAsync) : ConnectAndWriteAsync();
        await accept;
        var data = new byte[1];
        await server.ReadExactlyAsync(data, timeout.Token);
        await clientTask;
        Assert.Equal(42, data[0]);

        async Task ConnectAndWriteAsync()
        {
            if (!OperatingSystem.IsWindows()) { return; }
            using var clientIdentity = WindowsIdentity.GetCurrent();
            Assert.Equal(!elevatedServer, new WindowsPrincipal(clientIdentity).IsInRole(WindowsBuiltInRole.Administrator));
            using var client = await WindowsPipe.ConnectAsync(name, timeout.Token);
            await client.WriteAsync(new byte[] { 42 }, timeout.Token);
        }
    }

    [Fact]
    public void Server_rejects_an_existing_pipe_with_a_broad_acl()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var name = "atf-test-" + Guid.NewGuid().ToString("N");
        using var other = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
            using var server = WindowsPipe.CreateServer(name);
        });
    }

    [Fact]
    public async Task Wrong_owner_is_reported_by_request_and_readiness_clients()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { return; }
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "atf-pipe-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(0, InitCommand.Run(path, true, null, null));
            var state = StateDirectory.Open(path);
            var security = new PipeSecurity();
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            using var owner = DaemonLock.TryAcquire(state.LockFile);
            Assert.NotNull(owner);
            owner.WriteOwnerPid();
            using (var server = NamedPipeServerStreamAcl.Create(state.Socket, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
            {
                var response = await new IpcClient(state, new SpikeLimits()).SendAsync(new IpcRequest { Op = "job_list" }, CancellationToken.None);
                Assert.False(response.Ok);
                Assert.Equal(WindowsPipe.AccessDeniedMessage, response.Error);
            }
            using (var server = NamedPipeServerStreamAcl.Create(state.Socket, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
            {
                Assert.Equal(1, await SetupCommand.StartAsync(new Dictionary<string, string> { ["state-dir"] = path }, quiet: true));
            }
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    // Also works on elevated test machines without a linked UAC token.
    static SafeAccessTokenHandle CreateMediumToken(SafeAccessTokenHandle original)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var medium = new SecurityIdentifier("S-1-16-8192");
        var sid = Marshal.AllocHGlobal(Math.Max(admin.BinaryLength, medium.BinaryLength));
        var label = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
        try
        {
            var bytes = new byte[admin.BinaryLength];
            admin.GetBinaryForm(bytes, 0);
            Marshal.Copy(bytes, 0, sid, bytes.Length);
            var disabled = new SidAndAttributes { Sid = sid };
            Assert.True(CreateRestrictedToken(original, 1, 1, ref disabled, 0, 0, 0, 0, out var token));
            try
            {
                bytes = new byte[medium.BinaryLength];
                medium.GetBinaryForm(bytes, 0);
                Marshal.Copy(bytes, 0, sid, bytes.Length);
                Marshal.StructureToPtr(new SidAndAttributes { Sid = sid, Attributes = 0x20 }, label, false);
                Assert.True(SetTokenInformation(token, 25, label, Marshal.SizeOf<SidAndAttributes>() + medium.BinaryLength));
                return token;
            }
            catch { token.Dispose(); throw; }
        }
        finally { Marshal.FreeHGlobal(label); Marshal.FreeHGlobal(sid); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

#pragma warning disable SYSLIB1054 // Test project does not enable unsafe code for generated interop.
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disableCount,
        ref SidAndAttributes disable, uint deleteCount, nint delete, uint restrictCount, nint restrict, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int informationClass, nint information, int length);
}
