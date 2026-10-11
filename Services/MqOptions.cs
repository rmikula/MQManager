namespace MQManager.Services;

public class MqOptions
{
    public const string SectionName = "MQ";

    public string QueueManager { get; set; } = "QM1";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1414;
    public string Channel { get; set; } = "DEV.APP.SVRCONN";
    public string? User { get; set; }
    public string? Password { get; set; }
    public string DefaultQueue { get; set; } = "DEV.QUEUE.1";
    public int MaxBrowse { get; set; } = 100;

    /// <summary>Fronta příkazového serveru pro PCF dotazy (seznam front).</summary>
    public string CommandQueue { get; set; } = "SYSTEM.ADMIN.COMMAND.QUEUE";

    /// <summary>Modelová fronta, ze které se vytváří dočasná fronta pro odpovědi PCF.</summary>
    public string ModelQueue { get; set; } = "SYSTEM.DEFAULT.MODEL.QUEUE";
}
