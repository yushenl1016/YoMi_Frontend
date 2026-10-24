using System.Text.Json.Nodes;
using YoMi_Frontend.Services;
int checks=0;
void Check(bool good,string message) { checks++; if(!good) throw new Exception(message); }
void Reject(Action action,string message) { try { action(); throw new Exception(message); } catch(ArgumentException) { checks++; } catch(DesktopEconomyConflictException) { checks++; } }
var p=DesktopPetEconomy.NewProfile();
p["focus"]["coins"]=6543L;
var now=DateTimeOffset.Parse("2026-10-24T00:00:00Z");
DesktopPetEconomy.Apply(p,"focus",new JsonObject(),now,1800);
Check(DesktopPetEconomy.Number(p["focus"]["coins"])==6588,"45 coins per 30 minutes");
Check(DesktopPetEconomy.Number(p["focus"]["unclaimedRewards"])==1,"one supply box");
DesktopPetEconomy.Apply(p,"claim",new JsonObject(),now);
Check(DesktopPetEconomy.Number(p["focus"]["coins"])==6603,"15 supply coins");
Reject(()=>DesktopPetEconomy.Apply(p,"claim",new JsonObject(),now),"cannot claim missing box");
Check(DesktopPetEconomy.Catalog.Count==37,"37 catalog items");
DesktopPetEconomy.Apply(p,"purchase",new JsonObject {["itemId"]="outfit-sailor"},now);
Check(DesktopPetEconomy.Number(p["focus"]["coins"])==6243,"catalog price");
Reject(()=>DesktopPetEconomy.Apply(p,"purchase",new JsonObject {["itemId"]="outfit-sailor"},now),"cannot buy twice");
Reject(()=>DesktopPetEconomy.Apply(p,"purchase",new JsonObject {["itemId"]="unknown"},now),"unknown items rejected");
Reject(()=>DesktopPetEconomy.Apply(p,"purchase",new JsonObject {["itemId"]="outfit-maid",["price"]=0},now),"client price rejected");
Reject(()=>DesktopPetEconomy.Apply(p,"equip",new JsonObject {["itemId"]="outfit-maid"},now),"unowned equip rejected");
DesktopPetEconomy.Apply(p,"purchase",new JsonObject {["itemId"]="outfit-maid"},now);
Check(p["equipped"].AsArray().Count(x=>x.ToString().StartsWith("outfit-"))==1,"one outfit slot");
DesktopPetEconomy.Apply(p,"purchase",new JsonObject {["itemId"]="face-happy",["slot"]="typing"},now);
Check(p["equipped"].AsArray().Any(x=>x.ToString()=="typing:face-happy"),"typing face slot");
DesktopPetEconomy.Apply(p,"equip",new JsonObject {["clearSlot"]="typing"},now);
Check(!p["equipped"].AsArray().Any(x=>x.ToString().StartsWith("typing:")),"clear face slot");
foreach(var game in DesktopPetEconomy.GameMinimums)
    for(var i=0;i<3;i++)
    {
        var difficulty=new[]{"easy","medium","hard"}[i]; var threshold=game.Value[i];
        Check(DesktopPetEconomy.GameReward(game.Key,difficulty,threshold,true)==new[]{12,20,30}[i],game.Key+" win threshold");
        Check(DesktopPetEconomy.GameReward(game.Key,difficulty,threshold-1,true)==(threshold-1>=15?3:0),game.Key+" below threshold");
        Check(DesktopPetEconomy.GameReward(game.Key,difficulty,14,false)==0,game.Key+" no premature consolation");
        Check(DesktopPetEconomy.GameReward(game.Key,difficulty,15,false)==3,game.Key+" consolation");
    }
var tickets=DesktopPetEconomy.NewProfile(); tickets["games"]["day"]="2026-10-24"; tickets["games"]["used"]=9; tickets["games"]["bonus"]=1;
var gameInput=new JsonObject {["gameId"]="schulte",["difficulty"]="easy"};
DesktopPetEconomy.Apply(tickets,"game-start",gameInput,now);
Check(DesktopPetEconomy.Number(tickets["games"]["bonus"])==0,"paid ticket only after free tickets");
Reject(()=>DesktopPetEconomy.Apply(tickets,"game-start",gameInput,now),"out of tickets rejected");
DesktopPetEconomy.Apply(tickets,"game-start",gameInput,DateTimeOffset.Parse("2026-10-24T16:00:00Z"));
Check(tickets["games"]["day"].ToString()=="2026-10-25" && DesktopPetEconomy.Number(tickets["games"]["used"])==1,"Taipei midnight resets free tickets");
DesktopPetEconomy.Apply(tickets,"tickets",new JsonObject {["quantity"]=2},now);
Check(DesktopPetEconomy.Number(tickets["focus"]["coins"])==100 && DesktopPetEconomy.Number(tickets["games"]["bonus"])==2,"10 coins per ticket");
Reject(()=>DesktopPetEconomy.Apply(tickets,"tickets",new JsonObject {["quantity"]=21},now),"ticket purchase maximum");
Reject(()=>DesktopPetEconomy.Apply(tickets,"tickets",new JsonObject {["quantity"]=1.5},now),"fractional ticket rejected");
Reject(()=>DesktopPetEconomy.Apply(tickets,"preferences",new JsonObject {["focus"]=new JsonObject {["coins"]=999}},now),"preferences cannot set balance");
DesktopPetEconomy.Apply(tickets,"preferences",new JsonObject {["profile"]=new JsonObject {["displayName"]="新名字"},["baseLayout"]=JsonNode.Parse("{\"placements\":[{\"id\":\"neon-lamp\",\"x\":999,\"y\":0},{\"id\":\"decor-plant\"}]}")},now);
Check(tickets["profile"]["displayName"].ToString()=="新名字","name preference");
Check(tickets["baseLayout"]["placements"].AsArray().Count==1 && tickets["baseLayout"]["placements"][0]["x"].GetValue<double>()==.96,"scene owns decor and caps position");
Console.WriteLine($"Economy pure checks passed ({checks} assertions)");
if(args.Contains("--sql")) await SqlChecks.Run();
