using FugaTES.Callback.Http.ManualRetry;

public sealed class AcmCommandPassfileMetadataTests
{
    static readonly UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    static readonly UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Fact]
    public void Private_file_guard_exposes_validation_without_opening_secret_content()
    {
        var method = typeof(PrivateCommandFile).GetMethod("ValidateMetadata", new[] { typeof(string), typeof(int), typeof(bool) });
        Assert.NotNull(method); Assert.Equal(typeof(void), method.ReturnType);
    }

    [Theory]
    [InlineData(0u, false)] [InlineData(1001u, false)] [InlineData(1001u, true)]
    public void Root_or_effective_uid_owned_private_passfile_and_trusted_ancestors_are_accepted_without_content(uint owner, bool readOnly)
    {
        var metadata = new MetadataFixture();
        metadata.Entries[metadata.Passfile] = metadata.Entries[metadata.Passfile] with { OwnerUid = owner, Mode = readOnly ? UnixFileMode.UserRead : PrivateMode };
        PrivateCommandFile.ValidateMetadata(metadata.Passfile, 65536, true, metadata);
        Assert.Equal(metadata.Entries.Count, metadata.Inspected.Count);
        Assert.All(metadata.Inspected, p => Assert.Contains(p, metadata.Entries.Keys));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupRead)] [InlineData(UnixFileMode.GroupWrite)] [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.OtherWrite)] [InlineData(UnixFileMode.UserExecute)] [InlineData(UnixFileMode.SetUser)]
    public void Passfile_with_nonprivate_mode_is_rejected(UnixFileMode unsafeMode)
    {
        var metadata = new MetadataFixture();
        metadata.Entries[metadata.Passfile] = metadata.Entries[metadata.Passfile] with { Mode = PrivateMode | unsafeMode };
        Assert.Equal("ACM_COMMAND_PRIVATE_MODE_REQUIRED", Assert.Throws<InvalidOperationException>(() => PrivateCommandFile.ValidateMetadata(metadata.Passfile, 65536, true, metadata)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Foreign_owned_passfile_or_ancestor_is_rejected(bool ancestor)
    {
        var metadata = new MetadataFixture(); var path = ancestor ? metadata.Parent : metadata.Passfile;
        metadata.Entries[path] = metadata.Entries[path] with { OwnerUid = 1002 };
        Assert.Equal("ACM_COMMAND_TRUSTED_OWNER_REQUIRED", Assert.Throws<InvalidOperationException>(() => PrivateCommandFile.ValidateMetadata(metadata.Passfile, 65536, true, metadata)).Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Symlink_passfile_or_ancestor_is_rejected(bool ancestor)
    {
        var metadata = new MetadataFixture(); var path = ancestor ? metadata.Parent : metadata.Passfile;
        metadata.Entries[path] = metadata.Entries[path] with { IsSymlink = true };
        Assert.Equal("ACM_COMMAND_SYMLINK_REJECTED", Assert.Throws<InvalidOperationException>(() => PrivateCommandFile.ValidateMetadata(metadata.Passfile, 65536, true, metadata)).Message);
    }

    [Theory]
    [InlineData(UnixFileMode.GroupWrite)] [InlineData(UnixFileMode.OtherWrite)]
    public void Writable_passfile_parent_is_rejected(UnixFileMode writeMode)
    {
        var metadata = new MetadataFixture(); metadata.Entries[metadata.Parent] = metadata.Entries[metadata.Parent] with { Mode = DirectoryMode | writeMode };
        Assert.Equal("ACM_COMMAND_PRIVATE_MODE_REQUIRED", Assert.Throws<InvalidOperationException>(() => PrivateCommandFile.ValidateMetadata(metadata.Passfile, 65536, true, metadata)).Message);
    }

    [Fact]
    public void Nonregular_provider_cannot_be_used_as_passfile()
    {
        var metadata = new MetadataFixture(); metadata.Entries[metadata.Passfile] = metadata.Entries[metadata.Passfile] with { IsRegularFile = false };
        Assert.Equal("ACM_COMMAND_REGULAR_FILE_REQUIRED", Assert.Throws<InvalidOperationException>(() => PrivateCommandFile.ValidateMetadata(metadata.Passfile, 65536, true, metadata)).Message);
    }

    // Synthetic Linux metadata enables the exact default policy on the Windows test platform.
    // This capability has no Read method and creates/opens no provider or secret file.
    sealed class MetadataFixture : ICommandFileMetadataReader
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "offline-acm-private-" + Guid.NewGuid().ToString("N"));
        public string Passfile => Path.Combine(Parent, "pgpass");
        public bool IsLinux => true;
        public uint EffectiveUid => 1001;
        public Dictionary<string, CommandFileMetadata> Entries { get; } = [];
        public List<string> Inspected { get; } = [];
        public MetadataFixture()
        {
            Entries[Passfile] = new(true, false, true, false, 100, PrivateMode, 1001);
            for (string? path = Parent; path is not null; path = Path.GetDirectoryName(path))
                Entries[path] = new(true, true, false, false, 0, DirectoryMode, 0);
        }
        public CommandFileMetadata Inspect(string path)
        { Inspected.Add(path); return Entries.TryGetValue(path, out var entry) ? entry : new(false, false, false, false, 0, 0, 0); }
    }
}
