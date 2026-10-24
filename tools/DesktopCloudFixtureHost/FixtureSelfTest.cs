using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

static class FixtureSelfTest
{
    static void Check(bool value,string name) { if(!value) throw new Exception("Fixture check failed: "+name); }
    public static async Task Run(string url,X509Certificate2 certificate,JsonNode[] members)
    {
        using var handler=new HttpClientHandler {ServerCertificateCustomValidationCallback=(_,cert,_,_)=>cert?.Thumbprint==certificate.Thumbprint};
        using var http=new HttpClient(handler) {BaseAddress=new Uri(url),Timeout=TimeSpan.FromSeconds(15)};
        var admin=members[0]["token"].GetValue<string>(); var user=members[1]["token"].GetValue<string>();
        async Task<(HttpStatusCode Status,JsonNode Body)> Request(HttpMethod method,string path,string token,object body=null)
        {
            using var request=new HttpRequestMessage(method,path); if(token!=null) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            if(body!=null) request.Content=JsonContent.Create(body);
            using var response=await http.SendAsync(request); var text=await response.Content.ReadAsStringAsync();
            return(response.StatusCode,string.IsNullOrWhiteSpace(text)?null:JsonNode.Parse(text));
        }
        var docs=await Request(HttpMethod.Get,"/api/desktop/documents",admin);
        Check(docs.Status==HttpStatusCode.OK && docs.Body["documents"]["profile"]["payload"]["focus"]["coins"].GetValue<int>()==6543,"real session/documents");
        Check(docs.Body["documents"]["music"]["payload"]["playlists"].AsArray().Count==1,"existing playlist");
        var boot=await Request(HttpMethod.Post,"/api/desktop/economy/bootstrap",admin,new {}); Check(boot.Status==HttpStatusCode.OK,"bootstrap");
        var operation=new {operationId=Guid.NewGuid().ToString(),type="preferences",payload=new {profile=new {displayName="Fixture Admin"}}};
        var operate=await Request(HttpMethod.Post,"/api/desktop/economy/operate",admin,operation); Check(operate.Status==HttpStatusCode.OK,"operate");
        var replay=await Request(HttpMethod.Post,"/api/desktop/economy/operate",admin,operation); Check(replay.Status==HttpStatusCode.OK && replay.Body["profile"]["revision"].ToJsonString()==operate.Body["profile"]["revision"].ToJsonString(),"replay");
        var oldPut=await Request(HttpMethod.Put,"/api/desktop/documents/profile",admin,new {revision=operate.Body["profile"]["revision"].GetValue<long>(),payload=new {focus=new {coins=99}}});
        Check(oldPut.Status==HttpStatusCode.Conflict && oldPut.Body["code"]?.ToString()=="upgrade_required","legacy PUT blocked");
        var announcements=await Request(HttpMethod.Get,"/api/desktop/announcements",null); Check(announcements.Status==HttpStatusCode.OK,"public announcements");
        var rev=announcements.Body["revision"].GetValue<long>();
        var forbidden=await Request(HttpMethod.Post,"/api/desktop/announcements",user,new {revision=rev,title="not admin",body="deny"}); Check(forbidden.Status==HttpStatusCode.Forbidden,"user forbidden");
        var created=await Request(HttpMethod.Post,"/api/desktop/announcements",admin,new {revision=rev,title="Fixture 公告",body="Local test",pinned=true});
        Check(created.Status==HttpStatusCode.OK,"admin create"); var id=created.Body["announcements"][0]["id"].GetValue<long>(); rev=created.Body["revision"].GetValue<long>();
        var updated=await Request(HttpMethod.Put,"/api/desktop/announcements/"+id,admin,new {revision=rev,title="Edited fixture",body="Local edited",pinned=false}); Check(updated.Status==HttpStatusCode.OK,"admin update");
        var deleted=await Request(HttpMethod.Delete,"/api/desktop/announcements/"+id,admin,new {revision=updated.Body["revision"].GetValue<long>()}); Check(deleted.Status==HttpStatusCode.OK,"admin delete");
        using var a=new ClientWebSocket(); using var b=new ClientWebSocket();
        a.Options.SetRequestHeader("Authorization","Bearer "+admin); b.Options.SetRequestHeader("Authorization","Bearer "+user);
        a.Options.RemoteCertificateValidationCallback=(_,cert,_,_)=>cert?.GetCertHashString()==certificate.Thumbprint; b.Options.RemoteCertificateValidationCallback=a.Options.RemoteCertificateValidationCallback;
        var ws=new Uri(url.Replace("https:","wss:")+"/api/desktop/rooms"); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await a.ConnectAsync(ws,timeout.Token); await b.ConnectAsync(ws,timeout.Token);
        async Task Send(ClientWebSocket socket,string name,object payload)=>await socket.SendAsync(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { @event=name,payload})),WebSocketMessageType.Text,true,timeout.Token);
        async Task<JsonNode> Event(ClientWebSocket socket,string expected)
        {
            for(var attempts=0;attempts<30;attempts++)
            {
                using var bytes=new MemoryStream(); var buffer=new byte[65536]; WebSocketReceiveResult chunk;
                do { chunk=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),timeout.Token); if(chunk.MessageType==WebSocketMessageType.Close) throw new Exception("Socket closed"); bytes.Write(buffer,0,chunk.Count); } while(!chunk.EndOfMessage);
                var message=JsonNode.Parse(bytes.ToArray()); if(message["event"]?.ToString()==expected) return message["payload"];
                if(message["event"]?.ToString()=="room:error") throw new Exception("Room error: "+message["payload"].ToJsonString());
            }
            throw new Exception("No event "+expected);
        }
        await Send(a,"room:create",new {title="Fixture room",name="Fixture Admin"}); var session=await Event(a,"room:session");
        await Send(b,"room:join",new {code=session["code"].ToString(),name="Fixture User"}); await Event(b,"room:session");
        var room=await Event(b,"room:update"); Check(room["members"].AsArray().Count==2,"two real sessions join");
        await Send(b,"chat:send",new {text="fixture hello"}); var chat=await Event(a,"chat:message"); Check(chat["text"].ToString()=="fixture hello","cross-client chat");
        await Send(b,"seat:take",new {seat=7}); var seated=await Event(b,"room:update"); Check(seated["members"].AsArray().Any(x=>x["seat"].GetValue<int>()==7),"seat sync");
        await a.CloseAsync(WebSocketCloseStatus.NormalClosure,"checks complete",timeout.Token); await b.CloseAsync(WebSocketCloseStatus.NormalClosure,"checks complete",timeout.Token);
        Console.WriteLine("HTTPS fixture self-test passed: real REST, administrator permissions, two WebSocket clients.");
    }
}
