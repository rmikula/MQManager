using System.Collections;
using IBM.WMQ;
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

public class MqService(IOptions<MqOptions> options, ILogger<MqService> logger)
{
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
