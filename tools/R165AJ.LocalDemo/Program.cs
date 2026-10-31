using System.Text.Json;

if(args.Length!=2 || !Path.IsPathFullyQualified(args[0]) || !Guid.TryParse(args[1],out var runId))
    throw new InvalidOperationException("Expected absolute export path and run GUID");
var path=args[0];Directory.CreateDirectory(Path.GetDirectoryName(path)!);
var sync=new object();var json=new JsonSerializerOptions(JsonSerializerDefaults.Web){WriteIndented=true};
var rows=new Dictionary<string,Dictionary<string,object?>>();
using var stop=new CancellationTokenSource();
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
void Export()
{
    lock(sync)
    {
        var doc=new{schema="R165AJ_LOCAL_SIMULATION_V1",runId,simulated=true,capturedUtc=DateTimeOffset.UtcNow,scenarios=rows.Values.ToArray()};
        LocalObservationFile.Publish(path,JsonSerializer.Serialize(doc,json));
    }
}
Export();
var heartbeat=Task.Run(async()=>{while(!stop.IsCancellationRequested){Export();await Task.Delay(500,stop.Token);}});
try
{
    using var readyTimeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
    while(!File.Exists(path+".monitor-ready")||File.ReadAllText(path+".monitor-ready").Trim()!=runId.ToString())
        await Task.Delay(200,readyTimeout.Token);
    await Task.Delay(1200,stop.Token);
    foreach(var (outcome,number) in new[]{("success","0000986701"),("http400","0000986702"),("unknown","0000986703")})
    {
        lock(sync)rows[outcome]=new(){["outcome"]=outcome,["contract"]=number,["createdUtc"]=DateTimeOffset.UtcNow,
            ["updatedUtc"]=DateTimeOffset.UtcNow,["history"]=new List<object>()};
        void Observe(string name,object facts)
        {
            lock(sync)
            {
                var row=rows[outcome];
                foreach(var property in JsonSerializer.SerializeToElement(facts,json).EnumerateObject())row[property.Name]=property.Value.Clone();
                row["event"]=name;row["updatedUtc"]=DateTimeOffset.UtcNow;
                ((List<object>)row["history"]!).Add(new{@event=name,utc=DateTimeOffset.UtcNow});
                Export();
            }
        }
        await R165AILocalAutomaticEndToEndTests.RunScenarioAsync(outcome,number,Observe,stop.Token,keepAlive:outcome=="unknown");
        await Task.Delay(1500,stop.Token);
    }
}
catch(OperationCanceledException)when(stop.IsCancellationRequested){}
catch(Exception ex)
{
    // Only synthetic harness failures; no corporate secrets are loaded by this process.
    File.WriteAllText(path+".failure",ex.ToString());throw;
}
finally{stop.Cancel();try{await heartbeat;}catch(OperationCanceledException){}}
