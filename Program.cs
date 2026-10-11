using System.Text;
using IBM.WMQ;
using MQManager.Services;

// Klient IBM MQ potřebuje i kódové stránky mimo Unicode (např. 1252, 1250)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MqOptions>(builder.Configuration.GetSection(MqOptions.SectionName));
builder.Services.AddSingleton<MqService>();

var app = builder.Build();

// Převod chyb MQ na srozumitelnou odpověď pro UI
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        MQException mqEx => (StatusCodes.Status502BadGateway,
            $"Chyba MQ: {mqEx.Message} (reason {mqEx.ReasonCode})"),
        ArgumentException or FormatException => (StatusCodes.Status400BadRequest, ex.Message),
        _ => (StatusCodes.Status500InternalServerError, ex?.Message ?? "Neznámá chyba")
    };
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/config", (MqService mq) => new { defaultQueue = mq.DefaultQueue });

api.MapGet("/queues", (MqService mq) => Results.Ok(mq.ListQueues()));

api.MapGet("/queues/{queue}/messages", (string queue, int? max, MqService mq) =>
    Results.Ok(mq.Browse(queue, max)));

api.MapPost("/queues/{queue}/messages", (string queue, PutRequest request, MqService mq) =>
{
    if (string.IsNullOrEmpty(request.Body))
        return Results.BadRequest(new { error = "Tělo zprávy nesmí být prázdné." });
    var id = mq.Put(queue, request);
    return Results.Created($"/api/queues/{queue}/messages/{id}", new { messageId = id });
});

api.MapDelete("/queues/{queue}/messages/{messageId}", (string queue, string messageId, MqService mq) =>
    mq.Delete(queue, messageId)
        ? Results.NoContent()
        : Results.NotFound(new { error = "Zpráva nebyla nalezena." }));

api.MapDelete("/queues/{queue}/messages", (string queue, MqService mq) =>
    Results.Ok(new { deleted = mq.Clear(queue) }));

app.Run();
