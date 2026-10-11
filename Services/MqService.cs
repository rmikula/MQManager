using System.Collections;
using IBM.WMQ;
using IBM.WMQ.PCF;
using Microsoft.Extensions.Options;

namespace MQManager.Services;

public record MqMessageDto(
    string MessageId,
    string CorrelationId,
    DateTime PutDateTime,
    string Format,
    int CharacterSet,
    int Length,
    string Body);

public record PutRequest(string Body, string? CorrelationId);

public record MqQueueDto(string Name, string Type, int? Depth, int? MaxDepth, string Description);

public class MqService(IOptions<MqOptions> options, ILogger<MqService> logger)
{
    private const string ReplyQueuePrefix = "MQMANAGER.";

    private readonly MqOptions _opt = options.Value;

    public string DefaultQueue => _opt.DefaultQueue;

    private MQQueueManager Connect()
    {
        var props = new Hashtable
        {
            { MQC.TRANSPORT_PROPERTY, MQC.TRANSPORT_MQSERIES_MANAGED },
            { MQC.HOST_NAME_PROPERTY, _opt.Host },
            { MQC.PORT_PROPERTY, _opt.Port },
            { MQC.CHANNEL_PROPERTY, _opt.Channel },
        };
        if (!string.IsNullOrEmpty(_opt.User))
        {
            props.Add(MQC.USER_ID_PROPERTY, _opt.User);
            props.Add(MQC.PASSWORD_PROPERTY, _opt.Password ?? "");
            props.Add(MQC.USE_MQCSP_AUTHENTICATION_PROPERTY, true);
        }
        return new MQQueueManager(_opt.QueueManager, props);
    }

    /// <summary>
    /// Seznam lokálních a alias front přes PCF (MQCMD_INQUIRE_Q).
    /// Vrací jen fronty, na které má uživatel právo DSP.
    /// </summary>
    public List<MqQueueDto> ListQueues()
    {
        using var qm = Connect();
        var cmdQueue = qm.AccessQueue(_opt.CommandQueue, MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING);
        // Dočasná dynamická fronta pro odpovědi příkazového serveru, po zavření zanikne
        var replyQueue = qm.AccessQueue(_opt.ModelQueue,
            MQC.MQOO_INPUT_EXCLUSIVE | MQC.MQOO_FAIL_IF_QUIESCING, null, ReplyQueuePrefix + "*", null);
        try
        {
            var pcf = new PCFMessage(MQC.MQCMD_INQUIRE_Q);
            pcf.AddParameter(MQC.MQCA_Q_NAME, "*");
            pcf.AddParameter(MQC.MQIACF_Q_ATTRS, new[]
            {
                MQC.MQCA_Q_NAME, MQC.MQIA_Q_TYPE, MQC.MQIA_CURRENT_Q_DEPTH,
                MQC.MQIA_MAX_Q_DEPTH, MQC.MQCA_Q_DESC
            });

            var request = new MQMessage
            {
                Format = MQC.MQFMT_ADMIN,
                MessageType = MQC.MQMT_REQUEST,
                ReplyToQueueName = replyQueue.Name,
                Expiry = 300 // 30 s
            };
            pcf.Write(request);
            cmdQueue.Put(request, new MQPutMessageOptions());

            var result = new List<MqQueueDto>();
            var unauthorized = 0;
            var gmo = new MQGetMessageOptions
            {
                Options = MQC.MQGMO_WAIT | MQC.MQGMO_FAIL_IF_QUIESCING,
                WaitInterval = 10_000,
                MatchOptions = MQC.MQMO_MATCH_CORREL_ID
            };
            while (true)
            {
                var reply = new MQMessage { CorrelationId = request.MessageId };
                replyQueue.Get(reply, gmo);
                var response = new PCFMessage(reply);

                if (response.GetCompCode() != MQC.MQCC_OK)
                {
                    var reason = response.GetReason();
                    // Na fronty bez práva DSP vrací příkazový server samostatnou chybovou odpověď – přeskočíme je
                    if (reason == MQC.MQRC_NOT_AUTHORIZED)
                        unauthorized++;
                    // Žádná fronta neodpovídá filtru – není chyba
                    else if (reason is not (MQC.MQRC_UNKNOWN_OBJECT_NAME or MQC.MQRCCF_NONE_FOUND))
                        throw new MQException(response.GetCompCode(), reason);
                }
                else
                {
                    var queue = ToQueueDto(response);
                    // Vlastní dočasné fronty pro odpovědi do seznamu nepatří
                    if (queue is not null && !queue.Name.StartsWith(ReplyQueuePrefix))
                        result.Add(queue);
                }

                if (response.GetControl() == MQC.MQCFC_LAST) break;
            }
            if (result.Count == 0 && unauthorized > 0)
                throw new MQException(MQC.MQCC_FAILED, MQC.MQRC_NOT_AUTHORIZED);
            if (unauthorized > 0)
                logger.LogDebug("Skipped {Count} queues without DSP authority", unauthorized);
            return result.OrderBy(q => q.Name, StringComparer.Ordinal).ToList();
        }
        finally
        {
            replyQueue.Close();
            cmdQueue.Close();
            qm.Disconnect();
        }
    }

    private static MqQueueDto? ToQueueDto(PCFMessage response)
    {
        var values = response.GetParameters().ToDictionary(p => p.Parameter, p => p.GetValue());
        var type = values.TryGetValue(MQC.MQIA_Q_TYPE, out var t) ? (int)t : 0;
        // Zprávy lze číst jen z lokálních front a aliasů (model/remote/cluster vynecháváme)
        var typeName = type switch
        {
            MQC.MQQT_LOCAL => "local",
            MQC.MQQT_ALIAS => "alias",
            _ => null
        };
        if (typeName is null) return null;

        return new MqQueueDto(
            ((string)values[MQC.MQCA_Q_NAME]).Trim(),
            typeName,
            values.TryGetValue(MQC.MQIA_CURRENT_Q_DEPTH, out var d) ? (int)d : null,
            values.TryGetValue(MQC.MQIA_MAX_Q_DEPTH, out var m) ? (int)m : null,
            values.TryGetValue(MQC.MQCA_Q_DESC, out var desc) ? ((string)desc).Trim() : "");
    }

    /// <summary>Nedestruktivní čtení (browse) zpráv z fronty.</summary>
    public List<MqMessageDto> Browse(string queueName, int? max = null)
    {
        var limit = max ?? _opt.MaxBrowse;
        var result = new List<MqMessageDto>();

        using var qm = Connect();
        var queue = qm.AccessQueue(queueName,
            MQC.MQOO_BROWSE | MQC.MQOO_INQUIRE | MQC.MQOO_FAIL_IF_QUIESCING);
        try
        {
            var gmo = new MQGetMessageOptions
            {
                Options = MQC.MQGMO_BROWSE_FIRST | MQC.MQGMO_NO_WAIT | MQC.MQGMO_CONVERT
            };
            while (result.Count < limit)
            {
                // S MQGMO_CONVERT queue manager převede textové zprávy do UTF-8
                var msg = new MQMessage { CharacterSet = 1208 };
                try
                {
                    queue.Get(msg, gmo);
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
                {
                    break;
                }
                result.Add(ToDto(msg));
                gmo.Options = MQC.MQGMO_BROWSE_NEXT | MQC.MQGMO_NO_WAIT | MQC.MQGMO_CONVERT;
            }
        }
        finally
        {
            queue.Close();
            qm.Disconnect();
        }
        return result;
    }

    /// <summary>Vložení textové zprávy do fronty. Vrací MessageId (hex).</summary>
    public string Put(string queueName, PutRequest request)
    {
        using var qm = Connect();
        var queue = qm.AccessQueue(queueName, MQC.MQOO_OUTPUT | MQC.MQOO_FAIL_IF_QUIESCING);
        try
        {
            var msg = new MQMessage
            {
                Format = MQC.MQFMT_STRING,
                CharacterSet = 1208 // UTF-8
            };
            if (!string.IsNullOrWhiteSpace(request.CorrelationId))
                msg.CorrelationId = FromHex(request.CorrelationId);
            msg.WriteString(request.Body);

            queue.Put(msg, new MQPutMessageOptions());
            logger.LogInformation("Put message to {Queue}", queueName);
            return ToHex(msg.MessageId);
        }
        finally
        {
            queue.Close();
            qm.Disconnect();
        }
    }

    /// <summary>Smazání (destruktivní get) konkrétní zprávy podle MessageId.</summary>
    public bool Delete(string queueName, string messageIdHex)
    {
        using var qm = Connect();
        var queue = qm.AccessQueue(queueName, MQC.MQOO_INPUT_AS_Q_DEF | MQC.MQOO_FAIL_IF_QUIESCING);
        try
        {
            var msg = new MQMessage { MessageId = FromHex(messageIdHex) };
            var gmo = new MQGetMessageOptions
            {
                Options = MQC.MQGMO_NO_WAIT | MQC.MQGMO_ACCEPT_TRUNCATED_MSG,
                MatchOptions = MQC.MQMO_MATCH_MSG_ID
            };
            try
            {
                queue.Get(msg, gmo, 0);
            }
            catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_TRUNCATED_MSG_ACCEPTED) { }
            catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
            {
                return false;
            }
            logger.LogInformation("Deleted message {Id} from {Queue}", messageIdHex, queueName);
            return true;
        }
        finally
        {
            queue.Close();
            qm.Disconnect();
        }
    }

    /// <summary>Smazání všech zpráv z fronty. Vrací počet smazaných.</summary>
    public int Clear(string queueName)
    {
        using var qm = Connect();
        var queue = qm.AccessQueue(queueName, MQC.MQOO_INPUT_AS_Q_DEF | MQC.MQOO_FAIL_IF_QUIESCING);
        var count = 0;
        try
        {
            var gmo = new MQGetMessageOptions
            {
                Options = MQC.MQGMO_NO_WAIT | MQC.MQGMO_ACCEPT_TRUNCATED_MSG
            };
            while (true)
            {
                try
                {
                    queue.Get(new MQMessage(), gmo, 0);
                }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_TRUNCATED_MSG_ACCEPTED) { }
                catch (MQException ex) when (ex.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE)
                {
                    break;
                }
                count++;
            }
            logger.LogInformation("Cleared {Count} messages from {Queue}", count, queueName);
            return count;
        }
        finally
        {
            queue.Close();
            qm.Disconnect();
        }
    }

    private static MqMessageDto ToDto(MQMessage msg)
    {
        var length = msg.MessageLength;
        string body;
        if (msg.Format.Trim() == MQC.MQFMT_STRING.Trim())
        {
            body = msg.ReadString(length);
        }
        else
        {
            var bytes = msg.ReadBytes(Math.Min(length, 4096));
            body = "[binární data] " + Convert.ToHexString(bytes);
        }
        return new MqMessageDto(
            ToHex(msg.MessageId),
            ToHex(msg.CorrelationId),
            DateTime.SpecifyKind(msg.PutDateTime, DateTimeKind.Utc), // MQ ukládá čas vložení v UTC
            msg.Format.Trim(),
            msg.CharacterSet,
            length,
            body);
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes);

    private static byte[] FromHex(string hex)
    {
        var bytes = Convert.FromHexString(hex.Trim());
        if (bytes.Length > MQC.MQ_MSG_ID_LENGTH)
            throw new ArgumentException("ID může mít maximálně 24 bajtů (48 hex znaků).");
        var padded = new byte[MQC.MQ_MSG_ID_LENGTH];
        bytes.CopyTo(padded, 0);
        return padded;
    }
}
