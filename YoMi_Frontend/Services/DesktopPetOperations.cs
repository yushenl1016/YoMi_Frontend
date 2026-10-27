using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace YoMi_Frontend.Services
{
    public sealed class DesktopPetOperations
    {
        readonly DbConnection connection;
        public DesktopPetOperations(DesktopPetStore store) { connection=store.Connection; }
        public DesktopPetOperations(DbConnection connection) { this.connection=connection; }
        async Task<DbCommand> Command(string sql, DbTransaction transaction=null, params (string,object)[] parameters)
        {
            if(connection.State!=ConnectionState.Open) await connection.OpenAsync();
            var command=connection.CreateCommand(); command.CommandText=sql; command.Transaction=transaction; command.CommandTimeout=15;
            foreach(var value in parameters)
            {
                var p = command.CreateParameter(); p.ParameterName=value.Item1; p.Value=value.Item2??DBNull.Value;
                // SqlClient otherwise rounds DateTime to legacy datetime precision, changing overlap boundaries.
                if(value.Item2 is DateTime) p.DbType=DbType.DateTime2;
                command.Parameters.Add(p);
            }
            return command;
        }
        async Task<DateTimeOffset> Now(DbTransaction tx) { using var cmd=await Command("SELECT SYSUTCDATETIME();",tx); return new DateTimeOffset(DateTime.SpecifyKind((DateTime)await cmd.ExecuteScalarAsync(),DateTimeKind.Utc)); }
        async Task<(JsonObject Payload,long Revision)> Profile(int member,DbTransaction tx)
        {
            using var cmd=await Command("SELECT Payload,Revision FROM dbo.YoMiDocuments WITH(UPDLOCK,HOLDLOCK) WHERE FrontendMemberId=@Member AND DocumentKind='profile';",tx,("@Member",member));
            using var reader=await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? (JsonNode.Parse(reader.GetString(0)) as JsonObject,reader.GetInt64(1)) : (null,0);
        }
        static bool Managed(JsonObject profile)=>profile?["_economy"] is JsonObject;
        async Task<object> Envelope(int member,JsonObject profile,long revision,DateTimeOffset now,JsonObject receipt=null)
        {
            using var cmd=await Command("SELECT Name FROM dbo.FrontendMember WHERE Id=@Member;",null,("@Member",member));
            var name=await cmd.ExecuteScalarAsync() as string;
            return new { ok=true, receipt, memberName=string.IsNullOrWhiteSpace(name)?"會員":name,
                profile=new { payload=DesktopPetDocuments.Project("profile",profile.ToJsonString()),revision },serverTime=now,authority=true };
        }
        async Task<long> Save(int member,JsonObject p,long revision,DbTransaction tx)
        {
            using var cmd=await Command(revision==0
                ? "INSERT dbo.YoMiDocuments(FrontendMemberId,DocumentKind,Payload) VALUES(@Member,'profile',@Payload);"
                : "UPDATE dbo.YoMiDocuments SET Payload=@Payload,Revision=Revision+1,UpdatedAtUtc=SYSUTCDATETIME() WHERE FrontendMemberId=@Member AND DocumentKind='profile';",tx,("@Member",member),("@Payload",p.ToJsonString()));
            await cmd.ExecuteNonQueryAsync(); return revision+1;
        }
        public async Task<object> Bootstrap(int member,JsonElement initial=default,string pendingFocusStartedAt=null)
        {
            if(connection.State!=ConnectionState.Open) await connection.OpenAsync();
            using var tx=await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            var (p,revision)=await Profile(member,tx); var now=await Now(tx);
            if(p==null)
            {
                if(initial.ValueKind!=JsonValueKind.Undefined && initial.ValueKind!=JsonValueKind.Null) { DesktopPetDocuments.Validate("profile",initial); p=JsonNode.Parse(initial.GetRawText()) as JsonObject; }
                else p=DesktopPetEconomy.NewProfile();
            }
            if(!Managed(p))
            {
                var since=pendingFocusStartedAt==null?now:DesktopPetEconomy.Timestamp(pendingFocusStartedAt);
                if(since>now) throw new ArgumentException("待結算專注不能開始於未來。");
                DesktopPetEconomy.CompleteProfile(p);
                // Activation never converts previously credited totals back into new rewards.
                p["_economy"]=new JsonObject { ["since"]=since.ToString("O"),["carrySeconds"]=DesktopPetEconomy.Number(p["focus"]?["activeSeconds"])%1800 };
                revision=await Save(member,p,revision,tx);
            }
            await tx.CommitAsync(); return await Envelope(member,p,revision,now);
        }
        public async Task<object> State(int member)
        {
            if(connection.State!=ConnectionState.Open) await connection.OpenAsync();
            using var tx=await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            var (p,revision)=await Profile(member,tx); var now=await Now(tx);
            if(!Managed(p)) throw new DesktopEconomyConflictException("請先啟用伺服器同步。","bootstrap_required");
            await tx.CommitAsync(); return await Envelope(member,p,revision,now);
        }
        static string Canonical(JsonNode node)
        {
            if(node is JsonObject obj) return "{"+string.Join(",",obj.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>JsonSerializer.Serialize(x.Key)+":"+Canonical(x.Value)))+"}";
            if(node is JsonArray array) return "["+string.Join(",",array.Select(Canonical))+"]";
            return node?.ToJsonString()??"null";
        }
        public async Task<object> Operate(int member,string operationId,string type,JsonElement payload)
        {
            if(!Guid.TryParseExact(operationId,"D",out var id) || id==Guid.Empty || type==null || type.Length>24 || payload.ValueKind!=JsonValueKind.Object || payload.GetRawText().Length>64000) throw new ArgumentException("操作格式不正確。");
            JsonObject input;
            try { input=JsonNode.Parse(payload.GetRawText()) as JsonObject; _=Canonical(input); }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException) { throw new ArgumentException("操作包含重複或無效欄位。"); }
            var hash=SHA256.HashData(Encoding.UTF8.GetBytes(type+"\n"+Canonical(input)));
            if(connection.State!=ConnectionState.Open) await connection.OpenAsync();
            using var tx=await connection.BeginTransactionAsync(IsolationLevel.Serializable);
            var (p,revision)=await Profile(member,tx); var now=await Now(tx);
            if(!Managed(p)) throw new DesktopEconomyConflictException("請先啟用伺服器同步。","bootstrap_required");
            using(var replay=await Command("SELECT RequestHash,Receipt FROM dbo.YoMiDesktopOperations WHERE FrontendMemberId=@Member AND OperationId=@Id;",tx,("@Member",member),("@Id",id)))
            using(var reader=await replay.ExecuteReaderAsync())
                if(await reader.ReadAsync())
                {
                    if(!CryptographicOperations.FixedTimeEquals((byte[])reader.GetValue(0),hash)) throw new DesktopEconomyConflictException("相同操作編號不能用於不同內容。","operation_id_reused");
                    var previous=JsonNode.Parse(reader.GetString(1)) as JsonObject; await reader.DisposeAsync(); await tx.CommitAsync();
                    return await Envelope(member,p,revision,now,previous);
                }
            DateTimeOffset? start=null,end=null; long seconds=0,keys=0; JsonObject run=null; bool coversFocus=false,coversKeys=false;
            if(type=="focus")
            {
                if(!DateTimeOffset.TryParse(DesktopPetEconomy.Text(input,"startedAt"),out var s) || !DateTimeOffset.TryParse(DesktopPetEconomy.Text(input,"endedAt"),out var e)) throw new ArgumentException("專注時間格式不正確。");
                var active=DesktopPetEconomy.Number(input["activeSeconds"]); var keyCount=DesktopPetEconomy.Number(input["keyCount"]);
                coversFocus=active>0; coversKeys=keyCount>0;
                if(e<=s || e-s>TimeSpan.FromDays(1) || e>now.AddSeconds(5) || active>Math.Ceiling((e-s).TotalSeconds) || keyCount>Math.Ceiling((e-s).TotalSeconds)*100) throw new ArgumentException("專注紀錄的時間或數量無效。");
                var since=DateTimeOffset.Parse(DesktopPetEconomy.Text((JsonObject)p["_economy"],"since"));
                start=s>since?s:since; end=e<now?e:now;
                var focusIntervals=new List<(DateTimeOffset Start,DateTimeOffset End)>(); var keyIntervals=new List<(DateTimeOffset Start,DateTimeOffset End)>();
                using(var cmd=await Command("SELECT FocusStartedAtUtc,FocusEndedAtUtc,Receipt FROM dbo.YoMiDesktopOperations WHERE FrontendMemberId=@Member AND FocusEndedAtUtc>@Start AND FocusStartedAtUtc<@End ORDER BY FocusStartedAtUtc;",tx,("@Member",member),("@Start",start.Value.UtcDateTime),("@End",end.Value.UtcDateTime)))
                using(var reader=await cmd.ExecuteReaderAsync()) while(await reader.ReadAsync())
                {
                    var interval=(new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0),DateTimeKind.Utc)),new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1),DateTimeKind.Utc)));
                    var previous=JsonNode.Parse(reader.GetString(2)).AsObject();
                    if(previous["coversFocus"]?.GetValue<bool>()??DesktopPetEconomy.Number(previous["acceptedSeconds"])>0) focusIntervals.Add(interval);
                    if(previous["coversKeys"]?.GetValue<bool>()??DesktopPetEconomy.Number(previous["acceptedKeys"])>0) keyIntervals.Add(interval);
                }
                double Fraction(List<(DateTimeOffset Start,DateTimeOffset End)> intervals)
                {
                    var cursor=start.Value; double uncovered=0;
                    foreach(var interval in intervals) { if(interval.Start>cursor) uncovered+=Math.Max(0,(Min(interval.Start,end.Value)-cursor).TotalSeconds); if(interval.End>cursor) cursor=interval.End; }
                    if(end>cursor) uncovered+=(end.Value-cursor).TotalSeconds;
                    return Math.Clamp(uncovered/(e-s).TotalSeconds,0,1);
                }
                // Preserve fractional accepted time across heartbeats instead of losing a second on every clock skew.
                var meta=(JsonObject)p["_economy"];
                var secondsTotal=active*Fraction(focusIntervals)+(meta["fractionSeconds"]?.GetValue<double>()??0);
                var keysTotal=keyCount*Fraction(keyIntervals)+(meta["fractionKeys"]?.GetValue<double>()??0);
                seconds=(long)Math.Floor(secondsTotal); keys=(long)Math.Floor(keysTotal);
                meta["fractionSeconds"]=secondsTotal-seconds; meta["fractionKeys"]=keysTotal-keys;
                if(end<=start) { start=null; end=null; }
            }
            if(type=="game-finish")
            {
                if(!Guid.TryParseExact(DesktopPetEconomy.Text(input,"runId"),"D",out var runId)) throw new ArgumentException("遊戲編號無效。");
                using var cmd=await Command("UPDATE dbo.YoMiDesktopOperations SET CompletedAtUtc=@Now OUTPUT inserted.Receipt WHERE FrontendMemberId=@Member AND OperationId=@Run AND OperationType='game-start' AND CompletedAtUtc IS NULL;",tx,("@Member",member),("@Run",runId),("@Now",now.UtcDateTime));
                var raw=await cmd.ExecuteScalarAsync(); if(raw is string text) run=JsonNode.Parse(text) as JsonObject;
            }
            var receipt=DesktopPetEconomy.Apply(p,type,input,now,seconds,keys,run); receipt["operationId"]=id.ToString("D");
            if(type=="focus") { receipt["coversFocus"]=coversFocus; receipt["coversKeys"]=coversKeys; }
            if(type=="game-start") receipt["runId"]=id.ToString("D");
            revision=await Save(member,p,revision,tx);
            using(var insert=await Command("INSERT dbo.YoMiDesktopOperations(FrontendMemberId,OperationId,OperationType,RequestHash,Receipt,CreatedAtUtc,FocusStartedAtUtc,FocusEndedAtUtc) VALUES(@Member,@Id,@Type,@Hash,@Receipt,@Now,@Start,@End);",tx,("@Member",member),("@Id",id),("@Type",type),("@Hash",hash),("@Receipt",receipt.ToJsonString()),("@Now",now.UtcDateTime),("@Start",start?.UtcDateTime),("@End",end?.UtcDateTime))) await insert.ExecuteNonQueryAsync();
            await tx.CommitAsync(); return await Envelope(member,p,revision,now,receipt);
        }
        static DateTimeOffset Min(DateTimeOffset a,DateTimeOffset b)=>a<b?a:b;
    }
}


