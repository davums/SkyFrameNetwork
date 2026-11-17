using Skyframe.Ledger.Node;
using Skyframe.Ledger.Node.Networking;
NodeOptions options;
try{options=NodeOptions.Parse(args);}
catch(ArgumentException ex)
{Console.Error.WriteLine(ex.Message);return;}

var builder=Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Skyframe Node");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<NodeRuntime>();
builder.Services.AddSingleton<NodeStorage>();
builder.Services.AddSingleton<NodeFileLog>();
builder.Services.AddSingleton<PeerNetwork>();
builder.Services.AddHostedService<ControlApiService>();
builder.Services.AddHostedService<PublicNodeApiService>();
builder.Services.AddHostedService<NodeWorker>();
await builder.Build().RunAsync();
