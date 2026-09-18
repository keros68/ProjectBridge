using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalProjectBridge.Core.Collaboration;
using LocalProjectBridge.Core.Gateway;
using LocalProjectBridge.Core.Security;

namespace LocalProjectBridge.Core.Tests;

public sealed class CollaborationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lpb-relay-test-").FullName;
    private readonly Guid _connection = Guid.NewGuid();
    private readonly ProjectRecord _project;
    private readonly ProjectAuthorizationRegistry _registry = new();
    private readonly CollaborationStore _store;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public CollaborationTests()
    {
        _project = new() { Name="relay", Path=Directory.CreateDirectory(Path.Combine(_root,"project")).FullName };
        _registry.ReplaceProjects([_project]);
        _store = new CollaborationStore(Path.Combine(_root,"queue"),()=>_now);
    }

    private CollaborationRequest Submit(Guid? id=null) => _store.Submit(_project,_connection,"请检查这个测试计划。","codex://threads/test-source",requestId:id);
    private CollaborationRequest Read(CollaborationRequest r,string client="web-a") => _store.ReadRemote(r.RequestId,r.AccessCode,_connection,client,_registry);
    private CollaborationRequest Reply(CollaborationRequest r,string text="测试计划可行。",string client="web-a")
        => _store.ReplyRemote(r.RequestId,r.AccessCode,_connection,client,_registry,text,r.TurnId);

    [Fact]
    public void RoundTrip_PersistsReplyAndRejectsConflictingRetry()
    {
        var request=Submit();
        Assert.StartsWith("|from_codex|:\n", CollaborationStore.BuildPrompt(request));
        Assert.Equal(CollaborationStatus.Pending,request.Status);
        Assert.Throws<CollaborationException>(()=>Reply(request));
        Assert.Equal(CollaborationStatus.Read,Read(request).Status);
        Assert.Equal(CollaborationStatus.Answered,Reply(request).Status);
        Assert.Equal(CollaborationReplyOrigin.McpTool, _store.Get(request.RequestId)!.ReplyOrigin);
        Assert.Equal(CollaborationStatus.Answered,Reply(request).Status);
        Assert.Equal("reply_conflict",Assert.Throws<CollaborationException>(()=>Reply(request,"另一份回复")).Code);
        using var second=new CollaborationStore(Path.Combine(_root,"queue"));
        Assert.Equal("测试计划可行。",second.Get(request.RequestId)!.Reply);
        Assert.Equal(CollaborationStatus.Answered, _store.Cancel(request.RequestId).Status);
        Assert.Equal("测试计划可行。", second.Get(request.RequestId)!.Reply);
    }

    [Fact]
    public void RemoteAccess_RequiresCodeConnectionIdentityAndAuthorizedProject()
    {
        var request=Submit();
        Assert.Throws<CollaborationException>(()=>_store.ReadRemote(request.RequestId,"wrong",_connection,"web-a",_registry));
        Assert.Throws<CollaborationException>(()=>_store.ReadRemote(request.RequestId,request.AccessCode,Guid.NewGuid(),"web-a",_registry));
        Assert.Throws<CollaborationException>(()=>_store.ReadRemote(request.RequestId,request.AccessCode,_connection,null,_registry));
        Read(request);
        Assert.Throws<CollaborationException>(()=>Reply(request,client:"web-b"));
        _project.AllowWebRead=false; _registry.ReplaceProjects([_project]);
        Assert.Throws<CollaborationException>(()=>Reply(request));
        Assert.Null(_store.Get(request.RequestId)!.Reply);
    }

    [Fact]
    public void RevocationAndRootChange_CannotExposeOldQuestion()
    {
        var request=Submit();
        _project.Path=Directory.CreateDirectory(Path.Combine(_root,"other")).FullName;
        _registry.ReplaceProjects([_project]);
        Assert.Throws<CollaborationException>(()=>Read(request));
        Assert.Equal(1,_store.CancelProject(_project.Id));
        _project.Path=Path.Combine(_root,"project"); _registry.ReplaceProjects([_project]);
        Assert.Throws<CollaborationException>(()=>Read(request));
        Assert.Equal(CollaborationStatus.Cancelled,_store.Get(request.RequestId)!.Status);
    }

    [Fact]
    public void ExpiryAndExplicitCancel_AreDurable()
    {
        var first=Submit(); var second=Submit();
        _store.Cancel(first.RequestId);
        _now=_now.AddHours(25);
        Assert.Throws<CollaborationException>(()=>Read(first));
        Assert.Throws<CollaborationException>(()=>Read(second));
        using var reopened=new CollaborationStore(Path.Combine(_root,"queue"),()=>_now);
        Assert.Equal(CollaborationStatus.Cancelled,reopened.Get(first.RequestId)!.Status);
        Assert.Equal(CollaborationStatus.Expired,reopened.Get(second.RequestId)!.Status);
    }

    [Fact]
    public async Task ConcurrentInstances_KeepOneRequestAndDoNotOverwriteReply()
    {
        using var second=new CollaborationStore(Path.Combine(_root,"queue"));
        var id=Guid.NewGuid();
        var requests=await Task.WhenAll(Task.Run(()=>Submit(id)),Task.Run(()=>second.Submit(_project,_connection,"请检查这个测试计划。","codex://threads/test-source",requestId:id)));
        Assert.Equal(requests[0].AccessCode,requests[1].AccessCode);
        Assert.Single(_store.List());
        Read(requests[0]);
        var answers=await Task.WhenAll(Task.Run(()=>Reply(requests[0])),Task.Run(()=>second.ReplyRemote(id,requests[0].AccessCode,_connection,"web-a",_registry,"测试计划可行。",requests[0].TurnId)));
        Assert.All(answers,r=>Assert.Equal(CollaborationStatus.Answered,r.Status));
        Assert.Throws<CollaborationException>(()=>second.Submit(_project,_connection,"different","codex://threads/test-source",requestId:id));
    }

    [Fact]
    public void InvalidInputAndCorruptJournal_AreRejectedWithoutReplacingData()
    {
        Assert.Throws<CollaborationException>(()=>_store.Submit(_project,_connection,"question","source","https://evil.example/c/123"));
        _project.AllowCodexAskWeb=false;
        Assert.Throws<CollaborationException>(()=>Submit());
        _project.AllowCodexAskWeb=true;
        var request=Submit();
        var path=Path.Combine(_root,"queue",request.RequestId.ToString("N")+".json");
        var corrupt=JsonNode.Parse(File.ReadAllText(path))!; corrupt["AccessCode"]=null;
        File.WriteAllText(path,corrupt.ToJsonString());
        Assert.Equal("store_corrupt",Assert.Throws<CollaborationException>(()=>Submit()).Code);
        Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["AccessCode"]);
    }

    [Fact]
    public async Task McpHttpRoundTrip_ReturnsToLocalStoreWithoutLeakingSecrets()
    {
        var request=Submit();
        var dispatcher=new McpDispatcher("relay-test","1",CollaborationTools.Create(_store,_registry,_connection));
        await using var server=new GatewayServer(dispatcher,new RedactingLogger(_root),trustAllRequestsAsRemote:true);
        await server.StartAsync();
        using var http=new HttpClient(new HttpClientHandler { UseProxy=false });
        async Task<JsonObject> Call(string name,object arguments)
        {
            var body=JsonSerializer.Serialize(new { jsonrpc="2.0",id=1,method="tools/call",@params=new { name,arguments } });
            using var response=await http.PostAsync(server.ListenUrl,new StringContent(body,Encoding.UTF8,"application/json"));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsObject();
        }
        var read=await Call("get_collaboration_request",new { request_id=request.RequestId,access_code=request.AccessCode });
        Assert.NotEqual(true,read["isError"]?.GetValue<bool>());
        Assert.DoesNotContain(request.AccessCode,read.ToJsonString());
        Assert.DoesNotContain("ProjectRoot",read.ToJsonString());
        var answer=await Call("reply_to_collaboration_request",new { request_id=request.RequestId,access_code=request.AccessCode,turn_id=request.TurnId,reply="已核对本地测试问题。" });
        Assert.NotEqual(true,answer["isError"]?.GetValue<bool>());
        Assert.Equal("已核对本地测试问题。",_store.Get(request.RequestId)!.Reply);
        var denied=await Call("get_collaboration_request",new { request_id=request.RequestId,access_code="wrong" });
        Assert.True(denied["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CollaborationToolAnnotations_DescribeReadAndReplySideEffects()
    {
        var dispatcher = new McpDispatcher("relay-test", "1", CollaborationTools.Create(_store, _registry, _connection));
        var response = await dispatcher.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = JsonNode.Parse(response!)!["result"]!["tools"]!.AsArray();
        var read = tools.Single(item => item!["name"]!.GetValue<string>() == "get_collaboration_request")!["annotations"]!;
        Assert.True(read["readOnlyHint"]!.GetValue<bool>());
        Assert.False(read["destructiveHint"]!.GetValue<bool>());
        Assert.False(read["openWorldHint"]!.GetValue<bool>());
        var reply = tools.Single(item => item!["name"]!.GetValue<string>() == "reply_to_collaboration_request")!["annotations"]!;
        Assert.False(reply["readOnlyHint"]!.GetValue<bool>());
        Assert.False(reply["destructiveHint"]!.GetValue<bool>());
        Assert.True(reply["idempotentHint"]!.GetValue<bool>());
    }

    [Fact]
    public void AutoConsult_CheckpointAndExactConversationBindingSurviveRestartAndStayIsolated()
    {
        const string sourceA = "codex://threads/source-a";
        const string sourceB = "codex://threads/source-b";
        var plan = _store.Submit(_project, _connection, "请规划一次低风险修复。", sourceA,
            kind: CollaborationKind.Plan);
        Assert.Throws<CollaborationException>(() => Read(plan));
        plan = _store.RecordSendIntent(plan.RequestId, "https://chatgpt.com/c/11111111-1111-1111-1111-111111111111");
        Assert.Equal(CollaborationDeliveryState.IntentRecorded, plan.DeliveryState);
        plan = _store.ConfirmMessageSent(plan.RequestId);
        Assert.Equal(CollaborationDeliveryState.MessageConfirmed,
            _store.RecordSendIntent(plan.RequestId, plan.ConversationUrl!).DeliveryState);
        Read(plan);
        Assert.Equal("turn_mismatch", Assert.Throws<CollaborationException>(() =>
            _store.ReplyRemote(plan.RequestId, plan.AccessCode, _connection, "web-a", _registry,
                "这是旧轮次回复。", "00000000000000000000000000000000")).Code);
        Reply(plan, "这是当前规划。");
        _store.ConfirmConversationBinding(plan.RequestId, _project.Id);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.PlanReceived, "execute the accepted plan");

        using var reopened = new CollaborationStore(Path.Combine(_root, "queue"));
        var restored = reopened.Get(plan.RequestId)!;
        Assert.Equal(CollaborationCheckpoint.PlanReceived, restored.Checkpoint);
        Assert.Equal("https://chatgpt.com/c/11111111-1111-1111-1111-111111111111",
            reopened.GetBinding(_connection, _project.Id, sourceA)!.ConversationUrl);
        Assert.Null(reopened.GetBinding(_connection, _project.Id, sourceB));
        var sourceBPlan = reopened.Submit(_project, _connection, "另一个任务不能串线。", sourceB,
            kind: CollaborationKind.Plan);
        Assert.Null(sourceBPlan.ConversationUrl);
    }

    [Fact]
    public void AutoConsult_ReviewRequiresAnsweredPlanAndRevocationCancelsPendingRequest()
    {
        const string source = "codex://threads/source-review";
        var plan = _store.Submit(_project, _connection, "规划。", source, kind: CollaborationKind.Plan);
        Assert.Equal("invalid_parent", Assert.Throws<CollaborationException>(() =>
            _store.Submit(_project, _connection, "复核。", source, kind: CollaborationKind.Review,
                parentRequestId: plan.RequestId, iteration: 1)).Code);
        _store.RecordSendIntent(plan.RequestId, "https://chatgpt.com/c/22222222-2222-2222-2222-222222222222");
        _store.ConfirmMessageSent(plan.RequestId);
        Read(plan);
        Reply(plan);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.PlanReceived);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.Executing);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.ExecutedLocal);
        var review = _store.Submit(_project, _connection, "复核真实差异。", source,
            kind: CollaborationKind.Review, parentRequestId: plan.RequestId, iteration: 1);
        Assert.Equal(plan.RequestId, review.ParentRequestId);
        Assert.Equal(1, _store.CancelProject(_project.Id));
        Assert.Equal(CollaborationStatus.Cancelled, _store.Get(review.RequestId)!.Status);
    }

    [Fact]
    public void AutoConsult_CurrentReturnsTerminalCheckpointInsteadOfStartingAgain()
    {
        const string source = "codex://threads/terminal-source";
        var plan = _store.Submit(_project, _connection, "规划。", source, kind: CollaborationKind.Plan);
        _store.RecordSendIntent(plan.RequestId, "https://chatgpt.com/c/33333333-3333-3333-3333-333333333333");
        _store.ConfirmMessageSent(plan.RequestId);
        Read(plan);
        Reply(plan);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.PlanReceived);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.Executing);
        _store.SetCheckpoint(plan.RequestId, CollaborationCheckpoint.ExecutedLocal);
        _now = _now.AddSeconds(1);
        var review = _store.Submit(_project, _connection, "复核。", source, kind: CollaborationKind.Review,
            parentRequestId: plan.RequestId, iteration: 1);
        _store.RecordSendIntent(review.RequestId, "https://chatgpt.com/c/33333333-3333-3333-3333-333333333333");
        _store.ConfirmMessageSent(review.RequestId);
        Read(review);
        Reply(review, "复核通过。");
        _store.SetCheckpoint(review.RequestId, CollaborationCheckpoint.Done);

        var current = _store.FindCurrent(_project.Id, _connection, source);
        Assert.Equal(review.RequestId, current!.RequestId);
        Assert.Equal(CollaborationCheckpoint.Done, current.Checkpoint);

        var blocked = _store.Submit(_project, _connection, "新目标。", "codex://threads/blocked-source",
            kind: CollaborationKind.Plan);
        _store.SetCheckpoint(blocked.RequestId, CollaborationCheckpoint.Blocked, "需要登录。 ");
        Assert.Equal(CollaborationCheckpoint.Blocked,
            _store.FindCurrent(_project.Id, _connection, blocked.Source)!.Checkpoint);
    }

    [Fact]
    public void BrowserVisibleReply_RequiresAuthenticatedReadAndExactCurrentContext()
    {
        const string url = "https://chatgpt.com/c/44444444-4444-4444-4444-444444444444";
        var request = _store.Submit(_project, _connection, "请规划。", "codex://threads/browser-visible",
            kind: CollaborationKind.Plan);
        Assert.Equal("message_not_confirmed", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, _connection,
                url, _registry, "可见计划。")).Code);

        _store.RecordSendIntent(request.RequestId, url);
        _store.ConfirmMessageSent(request.RequestId);
        Assert.Equal("not_read", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, _connection,
                url, _registry, "可见计划。")).Code);
        Read(request);
        Assert.Equal("turn_mismatch", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, new string('0', 32), _project.Id, _connection,
                url, _registry, "可见计划。")).Code);
        Assert.Equal("project_mismatch", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, Guid.NewGuid(), _connection,
                url, _registry, "可见计划。")).Code);
        Assert.Equal("conversation_mismatch", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, _connection,
                "https://chatgpt.com/c/55555555-5555-5555-5555-555555555555", _registry, "可见计划。")).Code);
        Assert.Equal("not_authorized", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, Guid.NewGuid(),
                url, _registry, "可见计划。")).Code);

        var accepted = _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id,
            _connection, url, _registry, "可见计划。");
        Assert.Equal(CollaborationStatus.Answered, accepted.Status);
        Assert.Equal(CollaborationReplyOrigin.BrowserVisible, accepted.ReplyOrigin);
        Assert.Equal(accepted, _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId,
            _project.Id, _connection, url, _registry, "可见计划。"));
        Assert.Equal("reply_conflict", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, _connection,
                url, _registry, "另一份计划。")).Code);

        using var reopened = new CollaborationStore(Path.Combine(_root, "queue"));
        Assert.Equal(CollaborationReplyOrigin.BrowserVisible, reopened.Get(request.RequestId)!.ReplyOrigin);
        Assert.Equal(CollaborationStatus.Answered, reopened.Cancel(request.RequestId).Status);
        Assert.Equal("可见计划。", reopened.Get(request.RequestId)!.Reply);
    }

    [Fact]
    public void BrowserVisibleReply_RejectsRevokedPermissionAndChangedRoot()
    {
        const string url = "https://chatgpt.com/c/66666666-6666-6666-6666-666666666666";
        var request = _store.Submit(_project, _connection, "请规划。", "codex://threads/browser-revoked",
            kind: CollaborationKind.Plan);
        _store.RecordSendIntent(request.RequestId, url);
        _store.ConfirmMessageSent(request.RequestId);
        Read(request);

        _project.AllowCodexAskWeb = false;
        _registry.ReplaceProjects([_project]);
        Assert.Equal("project_not_authorized", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, _connection,
                url, _registry, "可见计划。")).Code);

        _project.AllowCodexAskWeb = true;
        _project.Path = Directory.CreateDirectory(Path.Combine(_root, "changed-root")).FullName;
        _registry.ReplaceProjects([_project]);
        Assert.Equal("project_not_authorized", Assert.Throws<CollaborationException>(() =>
            _store.AcceptBrowserVisibleReply(request.RequestId, request.TurnId, _project.Id, _connection,
                url, _registry, "可见计划。")).Code);
    }

    [Fact]
    public void CodexSkillInstaller_UsesExplicitRelayPathAndPreservesExistingUserCopy()
    {
        var app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(_root, "profile")).FullName;
        var skillDirectory = Directory.CreateDirectory(Path.Combine(app, "skills", CodexSkillInstaller.SkillName)).FullName;
        var relay = Path.Combine(app, "ProjectBridge.Relay.exe");
        File.WriteAllText(relay, "fictional executable");
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), "relay: `{{PROJECTBRIDGE_RELAY_PATH}}`");

        var first = CodexSkillInstaller.Install(app, profile);
        Assert.True(first.Installed);
        Assert.Contains(relay, File.ReadAllText(first.Destination));
        File.WriteAllText(first.Destination, "user customization");

        var second = CodexSkillInstaller.Install(app, profile);
        Assert.False(second.Installed);
        Assert.Equal("user customization", File.ReadAllText(first.Destination));
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_root,true);
    }
}
