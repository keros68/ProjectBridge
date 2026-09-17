using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Gateway;

namespace LocalProjectBridge.Core.Tests;

public sealed class ProjectWriteToolsTests
{
    [Fact]
    public async Task AutoApply_WritesAllOperationsWithoutPerChangeConfirmation_AndSafelyRestores()
    {
        using var fixture = new WriteFixture();
        fixture.Leases.RevokeAll();
        fixture.Write("a.txt", "before");
        fixture.Write("move.txt", "move");
        fixture.Write("delete.txt", "backup");
        var lease = fixture.Service.GrantAutoApply(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, TimeSpan.FromHours(2));
        var change = await fixture.Prepare(
            Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")),
            Op("create", "new.txt", ("content", "new")),
            Op("rename", "move.txt", ("target_path", "moved.txt")),
            Op("delete", "delete.txt"));
        Assert.Equal("before", fixture.Read("a.txt"));
        Assert.True(File.Exists(fixture.Path("delete.txt")));
        var request = Guid.NewGuid();
        var applied = await fixture.Apply(change.ChangeId, request);
        Assert.Equal(ChangeStatus.Applied, applied.Status);
        Assert.Equal(lease.LeaseId, applied.AutoApplyLeaseId);
        Assert.False(applied.RequiresDeletionConfirmation);
        Assert.Equal("after", fixture.Read("a.txt"));
        Assert.Equal("new", fixture.Read("new.txt"));
        Assert.Equal("move", fixture.Read("moved.txt"));
        Assert.False(File.Exists(fixture.Path("delete.txt")));
        Assert.Equal(ChangeStatus.Applied, (await fixture.Apply(change.ChangeId, request)).Status);
        var another = await fixture.Prepare(Op("create", "second.txt", ("content", "second")));
        Assert.Equal(ChangeStatus.Applied, (await fixture.Apply(another.ChangeId)).Status);
        fixture.Write("a.txt", "human edit");
        var restored = await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, Guid.NewGuid());
        Assert.Equal(ChangeStatus.RestorePartial, restored.Status);
        Assert.Equal("human edit", fixture.Read("a.txt"));
        Assert.Equal("backup", fixture.Read("delete.txt"));
        Assert.Equal("move", fixture.Read("move.txt"));
        Assert.False(File.Exists(fixture.Path("new.txt")));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("disconnect")]
    [InlineData("expired")]
    [InlineData("restart")]
    [InlineData("permission")]
    [InlineData("root")]
    public async Task AutoApply_InvalidatedGrantCannotWriteOrReappear(string reason)
    {
        using var fixture = new WriteFixture();
        fixture.Service.GrantAutoApply(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, TimeSpan.FromMinutes(15));
        var change = await fixture.Prepare(Op("create", "new.txt", ("content", "new")));
        var service = fixture.Service;
        switch (reason)
        {
            case "revoke": service.RevokeAutoApply(fixture.Project.Id); break;
            case "disconnect": service.RevokeAllAutoApply(); break;
            case "expired": fixture.Advance(TimeSpan.FromMinutes(15)); break;
            case "restart": service = new ProjectWriteService(fixture.Registry, new WriteLeaseStore(), fixture.Journal); break;
            case "permission":
                fixture.Project.AllowWebRead = false;
                fixture.Registry.ReplaceProjects([fixture.Project]);
                fixture.Project.AllowWebRead = true;
                fixture.Registry.ReplaceProjects([fixture.Project]);
                break;
            case "root":
                var original = fixture.Project.Path;
                fixture.Project.Path = Directory.CreateDirectory(fixture.Path("other")).FullName;
                fixture.Registry.ReplaceProjects([fixture.Project]);
                fixture.Project.Path = original;
                fixture.Registry.ReplaceProjects([fixture.Project]);
                break;
        }
        Assert.Null(service.GetAutoApplyLease(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId));
        await Assert.ThrowsAsync<WriteOperationException>(() => service.ApplyChangeAsync(fixture.Project.Id,
            fixture.ConnectionId, fixture.ClientId, change.ChangeId, Guid.NewGuid()));
        Assert.False(File.Exists(fixture.Path("new.txt")));
    }

    [Fact]
    public async Task AutoApply_DoesNotAuthorizeAnotherProjectClientOrConnection_AndStillChecksConflicts()
    {
        using var fixture = new WriteFixture();
        fixture.Service.GrantAutoApply(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, TimeSpan.FromHours(1));
        var other = new ProjectRecord { Name = "B", Path = Directory.CreateDirectory(fixture.Path("B")).FullName };
        fixture.Registry.ReplaceProjects([fixture.Project, other]);
        Assert.NotNull(fixture.Service.GetAutoApplyLease(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId));
        foreach (var identity in new[] { (fixture.Project.Id, Guid.NewGuid(), fixture.ClientId),
                     (fixture.Project.Id, fixture.ConnectionId, "other-client"), (other.Id, fixture.ConnectionId, fixture.ClientId) })
        {
            var record = await fixture.Service.PrepareChangeAsync(identity.Item1, identity.Item2, identity.Item3,
                Guid.NewGuid(), new JsonArray(Op("create", "unauthorized.txt", ("content", "no"))));
            Assert.Null(fixture.Service.GetAutoApplyLease(identity.Item1, identity.Item2, identity.Item3));
            await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Service.ApplyChangeAsync(identity.Item1,
                identity.Item2, identity.Item3, record.ChangeId, Guid.NewGuid()));
        }
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        fixture.Write("a.txt", "human edit");
        Assert.Equal("hash_conflict", (await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Apply(change.ChangeId))).Code);
        Assert.Equal("human edit", fixture.Read("a.txt"));
        await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Prepare(Op("create", ".env", ("content", "secret"))));
        await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Prepare(Op("create", "../outside.txt", ("content", "no"))));
    }

    [Fact]
    public async Task AutoApply_RenewedGrantForChangedRootCannotRestoreOldRoot()
    {
        using var fixture = new WriteFixture();
        fixture.Service.GrantAutoApply(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, TimeSpan.FromHours(1));
        fixture.Write("a.txt", "before");
        var oldFile = fixture.Path("a.txt");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        await fixture.Apply(change.ChangeId);
        fixture.Project.Path = Directory.CreateDirectory(fixture.Path("new-root")).FullName;
        fixture.Registry.ReplaceProjects([fixture.Project]);
        fixture.Service.GrantAutoApply(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, TimeSpan.FromHours(1));
        var denied = await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Service.RestoreRemoteAsync(
            fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, change.ChangeId, Guid.NewGuid()));
        Assert.Equal("project_changed", denied.Code);
        Assert.Equal("after", File.ReadAllText(oldFile));
    }

    [Fact]
    public async Task RejectedLocalProposal_CannotLaterBeApplied()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        Assert.Equal(ChangeStatus.Rejected,(await fixture.Service.RejectLocallyAsync(change.ChangeId)).Status);
        Assert.Equal(ChangeStatus.Rejected,(await fixture.Service.RejectLocallyAsync(change.ChangeId)).Status);
        await Assert.ThrowsAsync<WriteOperationException>(()=>fixture.Service.ApplyLocallyAsync(change.ChangeId));
        Assert.Equal("before",fixture.Read("a.txt"));
    }

    [Fact]
    public async Task LocalApplication_IsVisibleToRemoteSubmitWithoutAnotherApproval()
    {
        using var fixture = new WriteFixture();
        fixture.Leases.RevokeAll();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        await fixture.Service.ApplyLocallyAsync(change.ChangeId);
        var observed = await fixture.Apply(change.ChangeId);
        Assert.Equal(ChangeStatus.Applied, observed.Status);
        Assert.Equal("after", fixture.Read("a.txt"));
    }

    [Fact]
    public async Task PatchPreview_IncludesTheActualChangeAfterLongUnchangedPrefix()
    {
        using var fixture = new WriteFixture();
        fixture.Write("long.txt", new string('x', 9000) + "\noriginal-target\n");
        var change = await fixture.Prepare(Op("patch", "long.txt", ("old_text", "original-target"), ("new_text", "visible-replacement")));
        Assert.Contains("original-target", change.Preview);
        Assert.Contains("visible-replacement", change.Preview);
    }

    [Fact]
    public async Task PreviewApplyAndRestore_HandlesPatchCreateRenameAndConfirmedDelete()
    {
        using var fixture = new WriteFixture();
        fixture.Write("patch.txt", "before\r\nline\r\n");
        fixture.Write("rename.txt", "move me\n");
        fixture.Write("delete.txt", "keep backup\n");

        var change = await fixture.Prepare(
            Op("patch", "patch.txt", ("old_text", "before"), ("new_text", "after")),
            Op("create", "created.txt", ("content", "new file\n")),
            Op("rename", "rename.txt", ("target_path", "moved.txt")),
            Op("delete", "delete.txt"));

        Assert.Equal(ChangeStatus.Prepared, change.Status);
        Assert.True(change.RequiresDeletionConfirmation);
        Assert.Contains("--- patch.txt", change.Preview);
        await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Apply(change.ChangeId));

        await fixture.Journal.ConfirmDeletionAsync(change.ChangeId);
        var applied = await fixture.Apply(change.ChangeId);
        Assert.Equal(ChangeStatus.Applied, applied.Status);
        Assert.Equal("after\r\nline\r\n", fixture.Read("patch.txt"));
        Assert.Equal("new file\n", fixture.Read("created.txt"));
        Assert.False(File.Exists(fixture.Path("rename.txt")));
        Assert.Equal("move me\n", fixture.Read("moved.txt"));
        Assert.False(File.Exists(fixture.Path("delete.txt")));

        var restored = await fixture.Service.RestoreLocallyAsync(change.ChangeId);
        Assert.Equal(ChangeStatus.Restored, restored.Status);
        Assert.Equal("before\r\nline\r\n", fixture.Read("patch.txt"));
        Assert.False(File.Exists(fixture.Path("created.txt")));
        Assert.Equal("move me\n", fixture.Read("rename.txt"));
        Assert.False(File.Exists(fixture.Path("moved.txt")));
        Assert.Equal("keep backup\n", fixture.Read("delete.txt"));
    }

    [Fact]
    public async Task ProposalDoesNotNeedLease_ButRemoteApplyRequiresLocalConfirmation()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "one");
        fixture.Leases.RevokeProject(fixture.Project.Id);
        fixture.Advance(TimeSpan.FromHours(2));
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "one"), ("new_text", "two")));
        Assert.Equal(ChangeStatus.Prepared, change.Status);
        Assert.Equal("one", fixture.Read("a.txt"));

        var denied = await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Apply(change.ChangeId));
        Assert.Equal("local_confirmation_required", denied.Code);
        Assert.Equal("one", fixture.Read("a.txt"));

        var applied = await fixture.Service.ApplyLocallyAsync(change.ChangeId);
        Assert.Equal(ChangeStatus.Applied, applied.Status);
        Assert.Equal("two", fixture.Read("a.txt"));
    }

    [Fact]
    public async Task PrepareRejectsTraversalSensitiveAndBinaryFiles()
    {
        using var fixture = new WriteFixture();
        fixture.Write("safe.txt", "safe");
        fixture.Write(".env", "SECRET=x");
        await File.WriteAllBytesAsync(fixture.Path("binary.dat"), [0, 1, 2, 3]);

        await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Prepare(
            Op("create", "../outside.txt", ("content", "x"))));
        await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Prepare(
            Op("patch", ".env", ("old_text", "x"), ("new_text", "y"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Prepare(
            Op("patch", "binary.dat", ("old_text", "x"), ("new_text", "y"))));
        Assert.False(File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fixture.Project.Path)!, "outside.txt")));
    }

    [Fact]
    public async Task ApplyRejectsHashConflictWithoutOverwrite()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "one");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "one"), ("new_text", "two")));
        fixture.Write("a.txt", "human edit");

        var error = await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Apply(change.ChangeId));
        Assert.Equal("hash_conflict", error.Code);
        Assert.Equal("human edit", fixture.Read("a.txt"));
    }

    [Fact]
    public async Task RepeatedRequestIds_ReturnExistingResultWithoutRepeatingWrites()
    {
        using var fixture = new WriteFixture(singleChange: true);
        fixture.Write("a.txt", "one");
        var prepareId = Guid.NewGuid();
        var operation = Op("patch", "a.txt", ("old_text", "one"), ("new_text", "two"));
        var first = await fixture.PrepareWithId(prepareId, operation);
        var repeated = await fixture.PrepareWithId(prepareId, operation);
        Assert.Equal(first.ChangeId, repeated.ChangeId);

        var applyId = Guid.NewGuid();
        var applied = await fixture.Apply(first.ChangeId, applyId);
        var retry = await fixture.Apply(first.ChangeId, applyId);
        Assert.Equal(ChangeStatus.Applied, retry.Status);
        Assert.Equal(applied.ChangeId, retry.ChangeId);
        Assert.Equal("two", fixture.Read("a.txt"));
        Assert.Equal(ChangeStatus.Applied, fixture.Service.GetChange(
            fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, first.ChangeId).Status);
        var second = await fixture.Prepare(Op("create", "second.txt", ("content", "proposal only")));
        Assert.Equal(ChangeStatus.Prepared, second.Status);
        Assert.False(File.Exists(fixture.Path("second.txt")));

        fixture.Leases.RevokeProject(fixture.Project.Id);
        var retryAfterRevoke = await fixture.Apply(first.ChangeId, applyId);
        Assert.Equal(ChangeStatus.Applied, retryAfterRevoke.Status);
    }

    [Fact]
    public async Task MultiFileFailure_RecordsPartialResultAndDoesNotClaimSuccess()
    {
        var injector = new BeforeIndexFailure(1);
        using var fixture = new WriteFixture(singleChange: true, injector: injector);
        fixture.Write("a.txt", "A");
        fixture.Write("b.txt", "B");
        var change = await fixture.Prepare(
            Op("patch", "a.txt", ("old_text", "A"), ("new_text", "A2")),
            Op("patch", "b.txt", ("old_text", "B"), ("new_text", "B2")));

        var result = await fixture.Apply(change.ChangeId);
        Assert.Equal(ChangeStatus.Partial, result.Status);
        Assert.Equal(ChangeFileStatus.Applied, result.Files[0].Status);
        Assert.Equal(ChangeFileStatus.Failed, result.Files[1].Status);
        Assert.Equal("A2", fixture.Read("a.txt"));
        Assert.Equal("B", fixture.Read("b.txt"));
        var next = await fixture.Prepare(Op("create", "third.txt", ("content", "proposal after failure")));
        Assert.Equal(ChangeStatus.Prepared, next.Status);
        Assert.False(File.Exists(fixture.Path("third.txt")));
    }

    [Fact]
    public async Task CrashIsDetectedWithoutReplay_AndRecoveryRestoresOnlyAppliedFiles()
    {
        var injector = new CrashAfterIndex(0);
        using var fixture = new WriteFixture(injector: injector);
        fixture.Write("a.txt", "A");
        fixture.Write("b.txt", "B");
        var change = await fixture.Prepare(
            Op("patch", "a.txt", ("old_text", "A"), ("new_text", "A2")),
            Op("patch", "b.txt", ("old_text", "B"), ("new_text", "B2")));

        await Assert.ThrowsAsync<SimulatedWriteProcessCrashException>(() => fixture.Apply(change.ChangeId));
        Assert.Equal("A2", fixture.Read("a.txt"));
        Assert.Equal("B", fixture.Read("b.txt"));

        var restartedJournal = new ChangeJournal(fixture.Journal.RootDirectory);
        var interrupted = restartedJournal.Get(change.ChangeId)!;
        Assert.Equal(ChangeStatus.NeedsRecovery, interrupted.Status);
        Assert.Equal(ChangeFileStatus.Applied, interrupted.Files[0].Status);
        Assert.Equal(ChangeFileStatus.Pending, interrupted.Files[1].Status);
        Assert.Equal("B", fixture.Read("b.txt"));

        var restartedService = new ProjectWriteService(fixture.Registry, new WriteLeaseStore(), restartedJournal);
        var restored = await restartedService.RestoreLocallyAsync(change.ChangeId);
        Assert.Equal(ChangeStatus.Restored, restored.Status);
        Assert.Equal("A", fixture.Read("a.txt"));
        Assert.Equal("B", fixture.Read("b.txt"));
    }

    [Fact]
    public async Task RestoreDoesNotOverwriteLaterHumanEdit()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "one");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "one"), ("new_text", "two")));
        Assert.Equal(ChangeStatus.Applied, (await fixture.Apply(change.ChangeId)).Status);
        fixture.Write("a.txt", "human after apply");

        var restored = await fixture.Service.RestoreLocallyAsync(change.ChangeId);
        Assert.Equal(ChangeStatus.RestorePartial, restored.Status);
        Assert.Equal(ChangeFileStatus.RestoreConflict, restored.Files.Single().Status);
        Assert.Equal("human after apply", fixture.Read("a.txt"));
    }

    [Fact]
    public async Task ToolsRequireTrustedGatewayIdentityAndReturnStableError()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "one");
        var tool = ProjectWriteTools.Create(fixture.Service, fixture.ConnectionId).Single(item => item.Name == "prepare_change");
        var arguments = new JsonObject
        {
            ["project_id"] = fixture.Project.Id.ToString("D"),
            ["request_id"] = Guid.NewGuid().ToString("D"),
            ["operations"] = new JsonArray(Op("patch", "a.txt", ("old_text", "one"), ("new_text", "two")))
        };
        var denied = await tool.Invoke(arguments, CancellationToken.None);
        Assert.True(denied["isError"]!.GetValue<bool>());
        Assert.Contains("unverified_client", denied.ToJsonString());

        using (GatewayRequestContext.Push(fixture.ClientId))
        {
            var prepared = await tool.Invoke(arguments, CancellationToken.None);
            Assert.False(prepared["isError"]!.GetValue<bool>());
        }
    }

    [Fact]
    public async Task CorruptJournalDisablesNewRemoteWrites()
    {
        var root = Directory.CreateTempSubdirectory("lpb-corrupt-journal-").FullName;
        try
        {
            var journalRoot = Directory.CreateDirectory(System.IO.Path.Combine(root, "changes", "broken")).Parent!.FullName;
            await File.WriteAllTextAsync(System.IO.Path.Combine(journalRoot, "broken", "record.json"), "{not-json");
            var project = new ProjectRecord
            {
                Name = "write",
                Path = Directory.CreateDirectory(System.IO.Path.Combine(root, "project")).FullName
            };
            File.WriteAllText(System.IO.Path.Combine(project.Path, "a.txt"), "one");
            var registry = new ProjectAuthorizationRegistry();
            registry.ReplaceProjects([project]);
            Assert.True(registry.TryGetWriteProject(project.Id, out _, out var version));
            var connectionId = Guid.NewGuid();
            var leases = new WriteLeaseStore();
            leases.Grant(project.Id, version, connectionId, "client-a", TimeSpan.FromMinutes(30));
            var journal = new ChangeJournal(System.IO.Path.Combine(root, "changes"));
            Assert.False(journal.IsHealthy);
            var service = new ProjectWriteService(registry, leases, journal);

            var error = await Assert.ThrowsAsync<WriteOperationException>(() => service.PrepareChangeAsync(
                project.Id, connectionId, "client-a", Guid.NewGuid(),
                new JsonArray(Op("patch", "a.txt", ("old_text", "one"), ("new_text", "two")))));
            Assert.Equal("change_journal_unavailable", error.Code);
            Assert.Equal("one", File.ReadAllText(System.IO.Path.Combine(project.Path, "a.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task PostMutationIoFailure_RemainsRecoverableAndConsumesSingleLease()
    {
        using var fixture = new WriteFixture(singleChange: true, injector: new OrdinaryFailureAfterMutation(0));
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));

        var result = await fixture.Apply(change.ChangeId);

        Assert.Equal("after", fixture.Read("a.txt"));
        Assert.Equal(ChangeStatus.NeedsRecovery, result.Status);
        Assert.Equal(ChangeFileStatus.Applied, result.Files.Single().Status);
        var next = await fixture.Prepare(Op("create", "second.txt", ("content", "proposal only")));
        Assert.Equal(ChangeStatus.Prepared, next.Status);
        Assert.False(File.Exists(fixture.Path("second.txt")));
        var restarted = new ChangeJournal(fixture.Journal.RootDirectory).Get(change.ChangeId)!;
        Assert.Equal(ChangeStatus.NeedsRecovery, restarted.Status);
        var restored = await fixture.Service.RestoreLocallyAsync(change.ChangeId);
        Assert.Equal(ChangeStatus.Restored, restored.Status);
        Assert.Equal("before", fixture.Read("a.txt"));
    }

    [Fact]
    public async Task AppliedJournalFailure_IsRecoveredFromDiskAndConsumesSingleLease()
    {
        using var fixture = new WriteFixture(singleChange: true, injector: new AppliedJournalFailure(0));
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));

        var result = await fixture.Apply(change.ChangeId);

        Assert.Equal("after", fixture.Read("a.txt"));
        Assert.Equal(ChangeStatus.NeedsRecovery, result.Status);
        Assert.Equal(ChangeFileStatus.Applied, result.Files.Single().Status);
        var next = await fixture.Prepare(Op("create", "second.txt", ("content", "proposal only")));
        Assert.Equal(ChangeStatus.Prepared, next.Status);
        Assert.False(File.Exists(fixture.Path("second.txt")));
        Assert.Equal(ChangeStatus.NeedsRecovery,
            new ChangeJournal(fixture.Journal.RootDirectory).Get(change.ChangeId)!.Status);
    }

    [Fact]
    public async Task CorruptStagedContent_IsRejectedBeforeProjectFileChanges()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        var staged = change.Files.Single().StagedFile!;
        await File.WriteAllTextAsync(System.IO.Path.Combine(fixture.Journal.RootDirectory,
            change.ChangeId.ToString("N"), staged), "CORRUPTED_STAGE");

        var error = await Assert.ThrowsAsync<WriteOperationException>(() => fixture.Apply(change.ChangeId));

        Assert.Equal("staged_content_invalid", error.Code);
        Assert.Equal("before", fixture.Read("a.txt"));
        Assert.Equal(ChangeStatus.Prepared, fixture.Journal.Get(change.ChangeId)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptOrMissingBackup_IsRejectedWithoutChangingAppliedFile(bool deleteBackup)
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        var applied = await fixture.Apply(change.ChangeId);
        var backup = System.IO.Path.Combine(fixture.Journal.RootDirectory, change.ChangeId.ToString("N"),
            applied.Files.Single().BackupFile!);
        if (deleteBackup) File.Delete(backup);
        else await File.WriteAllTextAsync(backup, "CORRUPTED_BACKUP");

        var restored = await fixture.Service.RestoreLocallyAsync(change.ChangeId);

        Assert.Equal(ChangeStatus.RestorePartial, restored.Status);
        Assert.Equal(ChangeFileStatus.RestoreConflict, restored.Files.Single().Status);
        Assert.Equal("after", fixture.Read("a.txt"));
    }

    [Fact]
    public async Task RestoreRequestId_IsPersistedAndRetryReturnsOriginalResult()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        await fixture.Apply(change.ChangeId);
        var requestId = Guid.NewGuid();

        var restored = await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, requestId);
        var retry = await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, requestId);

        Assert.Equal(ChangeStatus.Restored, restored.Status);
        Assert.Equal(restored.ChangeId, retry.ChangeId);
        Assert.Equal(requestId, fixture.Journal.Get(change.ChangeId)!.RestoreRequestId);
        Assert.Equal(change.ChangeId, fixture.Journal.FindRequest(requestId)!.ChangeId);
        Assert.Equal(change.ChangeId,
            new ChangeJournal(fixture.Journal.RootDirectory).FindRequest(requestId)!.ChangeId);
    }

    [Fact]
    public async Task ReusedRequestId_IsRejectedForDifferentChangeOrPrepareContent()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "A");
        var applyA = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "A"), ("new_text", "A2")));
        var applyB = await fixture.Prepare(Op("create", "b.txt", ("content", "B")));
        var applyRequest = Guid.NewGuid();
        await fixture.Apply(applyA.ChangeId, applyRequest);
        var restartedJournal = new ChangeJournal(fixture.Journal.RootDirectory);
        var restartedService = new ProjectWriteService(fixture.Registry, fixture.Leases, restartedJournal);

        var targetError = await Assert.ThrowsAsync<WriteOperationException>(
            () => restartedService.ApplyChangeAsync(fixture.Project.Id, fixture.ConnectionId,
                fixture.ClientId, applyB.ChangeId, applyRequest));
        Assert.Equal("request_id_conflict", targetError.Code);
        Assert.False(File.Exists(fixture.Path("b.txt")));

        var prepareRequest = Guid.NewGuid();
        var first = await fixture.PrepareWithId(prepareRequest,
            Op("create", "first.txt", ("content", "one")));
        var persistedJournal = new ChangeJournal(fixture.Journal.RootDirectory);
        var persistedService = new ProjectWriteService(fixture.Registry, fixture.Leases, persistedJournal);
        var contentError = await Assert.ThrowsAsync<WriteOperationException>(() => persistedService.PrepareChangeAsync(
            fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, prepareRequest,
            new JsonArray(Op("create", "other.txt", ("content", "two")))));
        Assert.Equal("request_id_conflict", contentError.Code);
        Assert.Equal(first.ChangeId, fixture.Journal.FindRequest(prepareRequest)!.ChangeId);

        var operationError = await Assert.ThrowsAsync<WriteOperationException>(() => persistedService.ApplyChangeAsync(
            fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, applyB.ChangeId, prepareRequest));
        Assert.Equal("request_id_conflict", operationError.Code);
    }

    [Fact]
    public async Task ConcurrentDuplicatePrepareRequest_ReturnsOnePersistedChange()
    {
        using var fixture = new WriteFixture();
        var requestId = Guid.NewGuid();
        var first = fixture.PrepareWithId(requestId, Op("create", "a.txt", ("content", "same")));
        var second = fixture.PrepareWithId(requestId, Op("create", "a.txt", ("content", "same")));

        var results = await Task.WhenAll(first, second);

        Assert.Equal(results[0].ChangeId, results[1].ChangeId);
        Assert.Single(fixture.Journal.List(fixture.Project.Id));
    }

    [Fact]
    public async Task ConcurrentDuplicateApplyRequest_MutatesOnceAndReturnsOneResult()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        var requestId = Guid.NewGuid();

        var results = await Task.WhenAll(
            fixture.Apply(change.ChangeId, requestId),
            fixture.Apply(change.ChangeId, requestId));

        Assert.Equal(results[0].ChangeId, results[1].ChangeId);
        Assert.Equal(ChangeStatus.Applied, results[0].Status);
        Assert.Equal("after", fixture.Read("a.txt"));
        Assert.Equal(requestId, fixture.Journal.Get(change.ChangeId)!.ApplyRequestId);
    }

    [Fact]
    public async Task RestoreRequestHistory_SurvivesSecondRestoreReloadAndFirstRetry()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        var applied = await fixture.Apply(change.ChangeId);
        var backup = System.IO.Path.Combine(fixture.Journal.RootDirectory, change.ChangeId.ToString("N"),
            applied.Files.Single().BackupFile!);
        await File.WriteAllTextAsync(backup, "CORRUPTED_BACKUP");
        var firstRequest = Guid.NewGuid();
        var secondRequest = Guid.NewGuid();

        var first = await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, firstRequest);
        Assert.Equal(ChangeStatus.RestorePartial, first.Status);
        Assert.Equal("after", fixture.Read("a.txt"));
        await File.WriteAllTextAsync(backup, "before", new System.Text.UTF8Encoding(false));
        var second = await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, secondRequest);
        Assert.Equal(ChangeStatus.Restored, second.Status);
        Assert.Equal("before", fixture.Read("a.txt"));

        var reloadedJournal = new ChangeJournal(fixture.Journal.RootDirectory);
        Assert.Equal(change.ChangeId, reloadedJournal.FindRequest(firstRequest)!.ChangeId);
        Assert.Equal(change.ChangeId, reloadedJournal.FindRequest(secondRequest)!.ChangeId);
        var reloadedService = new ProjectWriteService(fixture.Registry, fixture.Leases, reloadedJournal);
        var firstRetry = await reloadedService.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, firstRequest);
        Assert.Equal(ChangeStatus.Restored, firstRetry.Status);
        Assert.Equal(change.ChangeId, firstRetry.ChangeId);
    }

    [Fact]
    public async Task HistoricalRestoreRequestId_RejectsCrossOperationAndCrossTargetReuse()
    {
        using var fixture = new WriteFixture();
        fixture.Write("a.txt", "before");
        var change = await fixture.Prepare(Op("patch", "a.txt", ("old_text", "before"), ("new_text", "after")));
        var applied = await fixture.Apply(change.ChangeId);
        var backup = System.IO.Path.Combine(fixture.Journal.RootDirectory, change.ChangeId.ToString("N"),
            applied.Files.Single().BackupFile!);
        await File.WriteAllTextAsync(backup, "CORRUPTED_BACKUP");
        var firstRequest = Guid.NewGuid();
        await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, firstRequest);
        await File.WriteAllTextAsync(backup, "before", new System.Text.UTF8Encoding(false));
        await fixture.Service.RestoreRemoteAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, change.ChangeId, Guid.NewGuid());

        var reloadedJournal = new ChangeJournal(fixture.Journal.RootDirectory);
        var reloadedService = new ProjectWriteService(fixture.Registry, fixture.Leases, reloadedJournal);
        var operationError = await Assert.ThrowsAsync<WriteOperationException>(() => reloadedService.PrepareChangeAsync(
            fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, firstRequest,
            new JsonArray(Op("create", "other.txt", ("content", "must not be accepted")))));
        Assert.Equal("request_id_conflict", operationError.Code);
        Assert.False(File.Exists(fixture.Path("other.txt")));

        fixture.Write("b.txt", "B");
        var otherChange = await reloadedService.PrepareChangeAsync(fixture.Project.Id, fixture.ConnectionId,
            fixture.ClientId, Guid.NewGuid(),
            new JsonArray(Op("patch", "b.txt", ("old_text", "B"), ("new_text", "B2"))));
        await reloadedService.ApplyChangeAsync(fixture.Project.Id, fixture.ConnectionId, fixture.ClientId,
            otherChange.ChangeId, Guid.NewGuid());
        var targetError = await Assert.ThrowsAsync<WriteOperationException>(() => reloadedService.RestoreRemoteAsync(
            fixture.Project.Id, fixture.ConnectionId, fixture.ClientId, otherChange.ChangeId, firstRequest));
        Assert.Equal("request_id_conflict", targetError.Code);
        Assert.Equal("B2", fixture.Read("b.txt"));
    }

    private static JsonObject Op(string type, string path, params (string Name, string Value)[] values)
    {
        var operation = new JsonObject { ["type"] = type, ["path"] = path };
        foreach (var (name, value) in values) operation[name] = value;
        return operation;
    }

    private sealed class WriteFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("lpb-write-").FullName;
        private DateTimeOffset _now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

        public WriteFixture(bool singleChange = false, IWriteFaultInjector? injector = null)
        {
            Project = new ProjectRecord { Name = "write", Path = Directory.CreateDirectory(System.IO.Path.Combine(_root, "project")).FullName };
            Registry.ReplaceProjects([Project]);
            Journal = new ChangeJournal(System.IO.Path.Combine(_root, "changes"));
            Leases = new WriteLeaseStore(() => _now);
            Service = new ProjectWriteService(Registry, Leases, Journal, injector);
            Grant(singleChange);
        }

        public ProjectRecord Project { get; }
        public ProjectAuthorizationRegistry Registry { get; } = new();
        public ChangeJournal Journal { get; }
        public WriteLeaseStore Leases { get; }
        public ProjectWriteService Service { get; }
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public string ClientId { get; } = "client-a";

        public void Grant(bool singleChange = false)
        {
            Assert.True(Registry.TryGetWriteProject(Project.Id, out _, out var version));
            Leases.Grant(Project.Id, version, ConnectionId, ClientId, TimeSpan.FromHours(1), singleChange);
        }
        public void Advance(TimeSpan amount) => _now += amount;
        public string Path(string relative) => System.IO.Path.Combine(Project.Path, relative);
        public void Write(string relative, string content) => File.WriteAllText(Path(relative), content, new System.Text.UTF8Encoding(false));
        public string Read(string relative) => File.ReadAllText(Path(relative));
        public Task<ChangeRecord> Prepare(params JsonObject[] operations) => PrepareWithId(Guid.NewGuid(), operations);
        public Task<ChangeRecord> PrepareWithId(Guid requestId, params JsonObject[] operations)
            => Service.PrepareChangeAsync(Project.Id, ConnectionId, ClientId, requestId,
                new JsonArray(operations.Select(operation => operation.DeepClone()).ToArray()), CancellationToken.None);
        public Task<ChangeRecord> Apply(Guid changeId, Guid? requestId = null)
            => Service.ApplyChangeAsync(Project.Id, ConnectionId, ClientId, changeId, requestId ?? Guid.NewGuid(), CancellationToken.None);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
    }

    private sealed class BeforeIndexFailure(int index) : IWriteFaultInjector
    {
        public void BeforeMutation(Guid changeId, int fileIndex)
        {
            if (fileIndex == index) throw new IOException("simulated second-file failure");
        }
        public void AfterMutation(Guid changeId, int fileIndex) { }
    }

    private sealed class CrashAfterIndex(int index) : IWriteFaultInjector
    {
        public void BeforeMutation(Guid changeId, int fileIndex) { }
        public void AfterMutation(Guid changeId, int fileIndex)
        {
            if (fileIndex == index) throw new SimulatedWriteProcessCrashException("simulated process crash");
        }
    }

    private sealed class OrdinaryFailureAfterMutation(int index) : IWriteFaultInjector
    {
        public void BeforeMutation(Guid changeId, int fileIndex) { }
        public void AfterMutation(Guid changeId, int fileIndex)
        {
            if (fileIndex == index) throw new IOException("simulated post-write I/O failure");
        }
    }

    private sealed class AppliedJournalFailure(int index) : IWriteFaultInjector
    {
        public void BeforeMutation(Guid changeId, int fileIndex) { }
        public void AfterMutation(Guid changeId, int fileIndex) { }
        public void BeforeAppliedJournalUpdate(Guid changeId, int fileIndex)
        {
            if (fileIndex == index) throw new IOException("simulated applied journal failure");
        }
    }
}
