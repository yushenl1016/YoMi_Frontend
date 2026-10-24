using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using YoMi_Frontend.Services;
using YoMi_Admin.Library.Models;

static class SqlChecks
{
    static void Check(bool good,string message) { if(!good) throw new Exception(message); }
    static JsonElement Payload(string text)=>JsonDocument.Parse(text).RootElement.Clone();
    static JsonObject Json(object value)=>JsonSerializer.SerializeToNode(value).AsObject();
    static long Coins(JsonObject envelope)=>DesktopPetEconomy.Number(envelope["profile"]["payload"]["focus"]["coins"]);
    public static async Task Run()
    {
        var database="YoMiDesktopFixture_"+Guid.NewGuid().ToString("N");
        const string master="Server=(localdb)\\MSSQLLocalDB;Database=master;Integrated Security=true;Encrypt=false;TrustServerCertificate=true";
        using var admin=new SqlConnection(master); await admin.OpenAsync();
        async Task Admin(string sql) { using var cmd=new SqlCommand(sql,admin); await cmd.ExecuteNonQueryAsync(); }
        await Admin("CREATE DATABASE ["+database+"];");
        var cs=new SqlConnectionStringBuilder(master) { InitialCatalog=database }.ConnectionString;
        try
        {
            using var connection=new SqlConnection(cs); await connection.OpenAsync();
            async Task Execute(string sql) { using var cmd=new SqlCommand(sql,connection); await cmd.ExecuteNonQueryAsync(); }
            await Execute(@"CREATE TABLE dbo.FrontendMember(Id int PRIMARY KEY,HasLoggedInNewSite bit NOT NULL DEFAULT 1,PasswordHash nvarchar(200));
CREATE TABLE dbo.YoMiDocuments(FrontendMemberId int NOT NULL,DocumentKind varchar(20) NOT NULL,Payload nvarchar(max) NOT NULL,Revision bigint NOT NULL DEFAULT 1,UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),PRIMARY KEY(FrontendMemberId,DocumentKind));
CREATE TABLE dbo.YoMiSessions(FrontendMemberId int NOT NULL,GoogleSubject nvarchar(200) NOT NULL,TokenHash binary(32) NOT NULL,ExpiresAtUtc datetime2 NOT NULL,RevokedAtUtc datetime2 NULL);
INSERT dbo.FrontendMember(Id,PasswordHash) VALUES(1,'fixture'),(2,'fixture'),(3,'fixture'),(4,'fixture'),(5,'fixture'),(6,'fixture');
INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES(1,'profile',N'{""profile"":{""displayName"":""Existing"",""custom"":""kept""},""focus"":{""coins"":6543,""activeSeconds"":1790,""completedSessions"":99,""keyCount"":321,""unclaimedRewards"":2},""inventory"":[""neon-lamp"",""outfit-maid""],""equipped"":[""outfit-maid""],""games"":{""day"":"""",""used"":0,""bonus"":4},""baseLayout"":{""version"":2,""theme"":""yumi-esports"",""placements"":[]},""oldCustom"":""preserved internally""}');");
            var migration=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../tools/DesktopPetOperations.sql"));
            await Execute(await File.ReadAllTextAsync(migration)); await Execute(await File.ReadAllTextAsync(migration));
            DesktopPetOperations Ops()=>new(new SqlConnection(cs));
            var ops=Ops();
            var boot=Json(await ops.Bootstrap(1,Payload("{\"focus\":{\"coins\":1}}")));
            Check(Coins(boot)==6543,"bootstrap keeps cloud balance");
            Check(boot["profile"]["payload"]["profile"]["custom"].GetValue<string>()=="kept","existing custom progress retained");
            Check(boot["profile"]["payload"]["_economy"]==null,"internal authority hidden");
            Check(Json(await ops.Bootstrap(1))["profile"]["revision"].GetValue<long>()==2,"bootstrap only once");
            await Execute("UPDATE dbo.YoMiDocuments SET Payload=JSON_MODIFY(Payload,'$._economy.since','2026-01-01T00:00:00Z') WHERE FrontendMemberId=1;");
            var end=DateTimeOffset.UtcNow.AddSeconds(-10); var start=end.AddSeconds(-20);
            JsonElement Focus(DateTimeOffset s,DateTimeOffset e,int active)=>JsonSerializer.SerializeToElement(new {startedAt=s.ToString("O"),endedAt=e.ToString("O"),activeSeconds=active,keyCount=20});
            var a=Guid.NewGuid().ToString(); var first=Json(await ops.Operate(1,a,"focus",Focus(start,end,20)));
            Check(Coins(first)==6588,"cross-session focus credits exactly45");
            var b=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"claim",Payload("{}"))); Check(Coins(b)==6603,"claim credits15");
            var replay=Json(await ops.Operate(1,a,"focus",Focus(start,end,20)));
            Check(Coins(replay)==6603,"A/B/A includes CURRENT balance");
            Check(replay["receipt"].ToJsonString()==first["receipt"].ToJsonString(),"A/B/A retains original receipt");
            try { await ops.Operate(1,a,"claim",Payload("{}")); throw new Exception("mismatch accepted"); } catch(DesktopEconomyConflictException ex) { Check(ex.Code=="operation_id_reused","mismatch code"); }
            var overlap=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(start,end,20)));
            Check(overlap["receipt"]["acceptedSeconds"].GetValue<long>()==0,"overlap cannot recredit");
            var keyOnlyStart=start.AddMinutes(-5); var keyOnlyEnd=keyOnlyStart.AddSeconds(10);
            await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(keyOnlyStart,keyOnlyEnd,0));
            var otherDevice=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(keyOnlyStart,keyOnlyEnd,10)));
            Check(otherDevice["receipt"]["acceptedSeconds"].GetValue<long>()==10,"key-only interval never consumes another device focus");
            Check(otherDevice["receipt"]["acceptedKeys"].GetValue<long>()==0,"overlapping key count still deduplicates independently");
            var focusOnlyStart=start.AddMinutes(-6); var focusOnlyEnd=focusOnlyStart.AddSeconds(10);
            await ops.Operate(1,Guid.NewGuid().ToString(),"focus",JsonSerializer.SerializeToElement(new { startedAt=focusOnlyStart,endedAt=focusOnlyEnd,activeSeconds=10,keyCount=0 }));
            var otherKeys=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(focusOnlyStart,focusOnlyEnd,0)));
            Check(otherKeys["receipt"]["acceptedKeys"].GetValue<long>()==20,"focus-only interval never consumes another device keys");
            var partial=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(start.AddSeconds(-10),end,30)));
            Check(partial["receipt"]["acceptedSeconds"].GetValue<long>()==10,"partial overlap credits only uncovered time");
            var fractionalStart=start.AddMinutes(-10);
            long fractionalCredit=0;
            for(var index=0;index<2;index++)
            {
                var s=fractionalStart.AddSeconds(index*2);
                await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(s.AddMilliseconds(-500),s.AddMilliseconds(500),1));
                var fractional=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(s,s.AddSeconds(1),1)));
                fractionalCredit+=fractional["receipt"]["acceptedSeconds"].GetValue<long>();
            }
            Check(fractionalCredit==1,"fractional overlap time carries across heartbeat operations");
            var revision=Json(await ops.State(1))["profile"]["revision"].GetValue<long>();
            try { await ops.Operate(1,Guid.NewGuid().ToString(),"focus",Focus(end,end.AddHours(1),10)); throw new Exception("future accepted"); } catch(ArgumentException) { }
            Check(Json(await ops.State(1))["profile"]["revision"].GetValue<long>()==revision,"invalid operation rollback");
            var options=new DbContextOptionsBuilder().UseSqlServer(cs).Options; using var db=new DBContext(options);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string>{{"DesktopPet:TrustServerCertificate","true"}}).Build(); var store=new DesktopPetStore(db,config);
            try { await store.Put(1,"profile",Payload("{\"focus\":{\"coins\":999999}}"),revision); throw new Exception("legacy overwrite accepted"); } catch(DesktopEconomyConflictException ex) { Check(ex.Code=="upgrade_required","legacy upgrade code"); }
            Check(await store.Put(1,"music",Payload("{\"version\":1,\"favorites\":[],\"playlists\":[],\"settings\":{}}"),0)==1,"music continues");
            var session=Json(await store.CreateSession(1,"fixture")); Check(await store.Member(session["token"].GetValue<string>())==1,"Google subject session remains valid");
            await ops.Bootstrap(2,Payload("{\"focus\":{\"coins\":100},\"inventory\":[\"neon-lamp\"],\"equipped\":[],\"games\":{\"bonus\":0}}"));
            async Task<bool> Buy(string item) { try { await Ops().Operate(2,Guid.NewGuid().ToString(),"purchase",Payload("{\"itemId\":\""+item+"\"}")); return true; } catch(DesktopEconomyConflictException) { return false; } }
            var buyers=await Task.WhenAll(Buy("energy-cup"),Buy("decor-plant")); Check(buyers.Count(x=>x)==1,"two purchases serialize against one balance");
            Check(Coins(Json(await ops.State(2))) is 0 or 10,"no negative balance");
            var gameStart=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"game-start",Payload("{\"gameId\":\"2048\",\"difficulty\":\"hard\"}")));
            var runId=gameStart["receipt"]["runId"].GetValue<string>();
            await Execute("UPDATE dbo.YoMiDesktopOperations SET Receipt=JSON_MODIFY(Receipt,'$.startedAt','"+DateTimeOffset.UtcNow.AddSeconds(-125).ToString("O")+"') WHERE OperationId='"+runId+"';");
            try { await Ops().Operate(1,Guid.NewGuid().ToString(),"game-finish",Payload("{\"runId\":\""+runId+"\",\"won\":\"not boolean\"}")); throw new Exception("invalid finish accepted"); } catch(ArgumentException) { }
            var finish=Guid.NewGuid().ToString(); var finishBody=JsonSerializer.SerializeToElement(new { runId,won=true,finishedAt=DateTimeOffset.UtcNow.ToString("O") });
            var result=Json(await Ops().Operate(1,finish,"game-finish",finishBody));
            Check(result["receipt"]["coins"].GetValue<long>()==30,"new service instance settles durable game");
            var finishReplay=Json(await Ops().Operate(1,finish,"game-finish",finishBody)); Check(Coins(finishReplay)==Coins(result),"finish replay credits once");
            try { await Ops().Operate(1,Guid.NewGuid().ToString(),"game-finish",Payload("{\"runId\":\""+runId+"\",\"won\":true}")); throw new Exception("second finish accepted"); } catch(DesktopEconomyConflictException) { }
            try { await Ops().Operate(2,Guid.NewGuid().ToString(),"game-finish",Payload("{\"runId\":\""+runId+"\",\"won\":true}")); throw new Exception("other member accepted"); } catch(DesktopEconomyConflictException) { }
            // Whichever writer wins first, bootstrap's row lock must protect authority from all subsequent legacy writes.
            using var raceDb=new DBContext(options); var raceStore=new DesktopPetStore(raceDb,config);
            var legacy=Task.Run(async()=>{try { return await raceStore.Put(3,"profile",Payload("{\"focus\":{\"coins\":77}}"),0); }catch(DesktopEconomyConflictException){return null;} });
            await Task.WhenAll(legacy,Ops().Bootstrap(3));
            var raced=Json(await Ops().State(3)); Check(Coins(raced) is 77 or 120,"bootstrap/legacy race produces one authoritative baseline");
            try { await raceStore.Put(3,"profile",Payload("{\"focus\":{\"coins\":999}}"),raced["profile"]["revision"].GetValue<long>()); throw new Exception("race removed authority"); }catch(DesktopEconomyConflictException){}
            // A legacy cumulative number cannot be rewarded a second time on activation.
            await ops.Bootstrap(4,Payload("{\"focus\":{\"coins\":4321,\"activeSeconds\":3600,\"completedSessions\":2}}"));
            var old=Json(await ops.Operate(4,Guid.NewGuid().ToString(),"focus",Focus(start,end,20))); Check(Coins(old)==4321,"pre-activation focus and old totals never recredit");
            var offlineStart=DateTimeOffset.UtcNow.AddMinutes(-2);
            var offlineBoot=Json(await ops.Bootstrap(5,Payload("{\"focus\":{\"coins\":4321,\"activeSeconds\":3600,\"completedSessions\":2}}"),offlineStart.ToString("O")));
            Check(Coins(offlineBoot)==4321,"offline cutover preserves the existing balance");
            var offlineCredit=Json(await ops.Operate(5,Guid.NewGuid().ToString(),"focus",Focus(offlineStart,offlineStart.AddSeconds(20),20)));
            Check(offlineCredit["receipt"]["acceptedSeconds"].GetValue<long>()==20,"first offline queue survives initial bootstrap");
            Check(Coins(offlineCredit)==4321 && offlineCredit["profile"]["payload"]["focus"]["activeSeconds"].GetValue<long>()==20,"old cumulative total is never reminted by pending cutover");
            await ops.Bootstrap(5,default,offlineStart.AddHours(-1).ToString("O"));
            var tooEarly=Json(await ops.Operate(5,Guid.NewGuid().ToString(),"focus",Focus(offlineStart.AddSeconds(-20),offlineStart,20)));
            Check(tooEarly["receipt"]["acceptedSeconds"].GetValue<long>()==0,"existing authority cutoff cannot be moved by later bootstrap");
            foreach(var invalid in new[] { "not-an-iso-date",DateTimeOffset.UtcNow.AddHours(1).ToString("O") })
                try { await ops.Bootstrap(6,default,invalid); throw new Exception("invalid pending cutoff accepted"); } catch(ArgumentException) { }
            Check(Coins(Json(await ops.Bootstrap(6)))==120,"failed cutover rolls back a new profile");
            var offlineGame=Json(await ops.Operate(1,Guid.NewGuid().ToString(),"game-start",Payload("{\"gameId\":\"2048\",\"difficulty\":\"easy\"}")));
            var offlineRun=offlineGame["receipt"]["runId"].GetValue<string>(); var gameStartedAt=DateTimeOffset.UtcNow.AddMinutes(-1);
            await Execute("UPDATE dbo.YoMiDesktopOperations SET Receipt=JSON_MODIFY(Receipt,'$.startedAt','"+gameStartedAt.ToString("O")+"') WHERE OperationId='"+offlineRun+"';");
            foreach(var invalidTime in new[] { gameStartedAt.AddSeconds(-1),DateTimeOffset.UtcNow.AddHours(1) })
                try { await ops.Operate(1,Guid.NewGuid().ToString(),"game-finish",JsonSerializer.SerializeToElement(new { runId=offlineRun,won=true,finishedAt=invalidTime.ToString("O") })); throw new Exception("invalid game completion accepted"); } catch(ArgumentException) { }
            var earlyFinish=JsonSerializer.SerializeToElement(new { runId=offlineRun,won=true,finishedAt=gameStartedAt.AddSeconds(5).ToString("O") });
            var offlineFinishId=Guid.NewGuid().ToString(); var offlineReward=Json(await Ops().Operate(1,offlineFinishId,"game-finish",earlyFinish));
            Check(offlineReward["receipt"]["coins"].GetValue<long>()==0,"offline wait cannot inflate an early game outcome reward");
            Check(Coins(Json(await Ops().Operate(1,offlineFinishId,"game-finish",earlyFinish)))==Coins(offlineReward),"offline completion retry stays idempotent");
            Console.WriteLine("SQL checks passed: preserve/bootstrap, A/B/A, overlap, rollback, concurrent buyers, legacy race, music, sessions, durable games, member isolation.");
        }
        finally { SqlConnection.ClearAllPools(); await Admin("ALTER DATABASE ["+database+"] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ["+database+"];"); }
    }
}



