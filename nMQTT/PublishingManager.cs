using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nmqtt
{

    /// <summary>
    /// Handles the logic and workflow surrounding the message publishing and receipt process
    /// </summary>
    /// <remarks>
    /// <para>It's probably worth going into a bit of the detail around publishing and Quality of Service levels
    /// as they are primarily the reason why message publishing has been split out into this class.</para>
    /// <para>There are
    /// 3 different QOS levels. AtMostOnce (0), means that the message, when sent from broker to client, or 
    /// client to broker, should be delivered at most one time, and it does not matter if the message is
    /// "lost". QOS 2, AtLeastOnce, means that the message should be successfully received by the receiving
    /// party at least one time, so requires some sort of acknowledgement so the sender can re-send if the
    /// receiver does not acknowledge.</para>
    /// <para>QOS 3 is a bit more complicated as it provides the facility for guaranteed delivery of the message
    /// exactly one time, no more, no less.</para>
    /// <para>Each of these have different message flow between the sender and receiver.</para>
    /// <para><b>QOS 0 - AtMostOnce</b>
    /// <code>
    /// Sender --> Publish --> Receiver
    /// </code>
    /// </para> 
    /// <para><b>QOS 1 - AtLeastOnce</b>
    /// <code>
    /// Sender --> Publish --> Receiver --> PublishAck --> Sender
    ///                            |
    ///                            v
    ///                    Message Processor
    /// </code>
    /// </para>
    /// <para><b>QOS 2 - AtLeastOnce</b>
    /// <code>
    /// Sender --> Publish --> Receiver --> PublishReceived --> Sender --> PublishRelease --> Reciever --> PublishComplete --> Sender
    ///                                                                                          |
    ///                                                                                          v
    ///                                                                                   Message Processor
    /// </code>
    /// </para>
    /// </remarks>
    internal class PublishingManager
    {
        /// <summary>
        /// Stores messages that have been pubished but not yet acknowledged.
        /// </summary>
        private Dictionary<int, MqttPublishMessage> publishedMessages = new Dictionary<int, MqttPublishMessage>();

        /// <summary>
        /// Stores messages that have been received from a broker with qos level 2 (Exactly Once).
        /// </summary>
        private Dictionary<int, MqttPublishMessage> receivedMessages = new Dictionary<int, MqttPublishMessage>();

        /// <summary>
        /// Callback executed when a published message has been received from the broker.
        /// </summary>
        private Func<MqttPublishMessage, bool> publishMessageCallback;

        /// <summary>
        /// The current connection handler.
        /// </summary>
        private IMqttConnectionHandler connectionHandler;

        /// <summary>
        /// Stores a cache of data converters used when publishing data to a broker.
        /// </summary>
        private Dictionary<Type, IPublishDataConverter> dataConverters = new Dictionary<Type, IPublishDataConverter>();

        /// <summary>
        /// Initializes a new instance of the <see cref="PublishingManager"/> class.
        /// </summary>
        /// <param name="connectionHandler">The connection handler.</param>
        /// <param name="publishMessageCallback">The function that should be called when a publish message is received.</param>
        public PublishingManager(IMqttConnectionHandler connectionHandler, Func<MqttPublishMessage, bool> publishMessageCallback)
        {
            this.connectionHandler = connectionHandler;
            this.publishMessageCallback = publishMessageCallback;
            connectionHandler.RegisterForMessage(MqttMessageType.PublishAck, HandlePublishAcknowledgement);
            connectionHandler.RegisterForMessage(MqttMessageType.Publish, HandlePublish);
            connectionHandler.RegisterForMessage(MqttMessageType.PublishComplete, HandlePublishComplete);
            connectionHandler.RegisterForMessage(MqttMessageType.PublishRelease, HandlePublishRelease);
            connectionHandler.RegisterForMessage(MqttMessageType.PublishReceived, HandlePublishReceived);
        }

        /// <summary>
        /// Publish a message to the broker on the specified topic.
        /// </summary>
        /// <param name="topic">The topic to send the message to.</param>
        /// <param name="qualityOfService">The QOS to use when publishing the message.</param>
        /// <param name="data">The message to send.</param>
        /// <returns>The message identifier assigned to the message.</returns>
        public short Publish<TDataConverter>(string topic, MqttQos qualityOfService, object data)
            where TDataConverter : IPublishDataConverter
        {
            short msgID = MessageIdentifierDispenser.GetNextMessageIdentifier(String.Format("Topic:{0}", topic));

            IPublishDataConverter converter = GetPublishDataConverter<TDataConverter>();

            MqttPublishMessage msg = new MqttPublishMessage()
                .ToTopic(topic)
                .WithMessageIdentifier(msgID)
                .WithQos(qualityOfService)
                .PublishData(converter.ConvertToBytes(data));

            // QOS level 1 or 2 messages need to be saved so we can do the ack processes
            if (qualityOfService == MqttQos.AtLeastOnce || qualityOfService == MqttQos.ExactlyOnce)
            {
                publishedMessages.Add(msgID, msg);
            }

            connectionHandler.SendMessage(msg);

            return msgID;
        }

        /// <summary>
        /// Gets an instance of the specified publish data converter.
        /// </summary>
        /// <typeparam name="TDataConverter">The type of the data converter.</typeparam>
        /// <returns></returns>
        private IPublishDataConverter GetPublishDataConverter<TDataConverter>()
            where TDataConverter : IPublishDataConverter
        {
            IPublishDataConverter dataConverter;
            if (!dataConverters.TryGetValue(typeof(TDataConverter), out dataConverter))
            {
                dataConverter = Activator.CreateInstance<TDataConverter>();
                dataConverters.Add(typeof(TDataConverter), dataConverter);
            }

            return dataConverter;
        }

        /// <summary>
        /// Handles the receipt of publish acknowledgement messages.
        /// </summary>
        /// <param name="msg">The publish acknowledgement</param>
        /// <returns>True; always.</returns>
        /// <remarks>
        /// This callback simply removes it from the list of published messages.
        /// </remarks>
        private bool HandlePublishAcknowledgement(MqttMessage msg)
        {
            MqttPublishAckMessage ackMsg = (MqttPublishAckMessage)msg;

            // if we're expecting an ack for the message, remove it from the list of pubs awaiting ack.
            if (publishedMessages.Keys.Contains(ackMsg.VariableHeader.MessageIdentifier))
            {
                publishedMessages.Remove(ackMsg.VariableHeader.MessageIdentifier);
            }

            return true;
        }

        /// <summary>
        /// Handles the receipt of publish messages from a message broker.
        /// </summary>
        /// <param name="msg">The message that was published.</param>
        /// <returns></returns>
        private bool HandlePublish(MqttMessage msg)
        {
            MqttPublishMessage pubMsg = (MqttPublishMessage)msg;
            bool publishSuccess = false;

            if (pubMsg.Header.Qos == MqttQos.AtMostOnce)
            {
                // QOS AtMostOnce 0 require no response.
                
                // send the message for processing to whoever is waiting.
                publishSuccess = publishMessageCallback(pubMsg);
            }
            else if (pubMsg.Header.Qos == MqttQos.AtLeastOnce)
            {
                // QOS AtLeastOnce 1 require an acknowledgement

                // send the message for processing to whoever is waiting.
                publishSuccess = publishMessageCallback(pubMsg);

                MqttPublishAckMessage ackMsg = new MqttPublishAckMessage()
                    .WithMessageIdentifier(pubMsg.VariableHeader.MessageIdentifier);
                connectionHandler.SendMessage(ackMsg);
            }
            else if (pubMsg.Header.Qos == MqttQos.ExactlyOnce)
            {
                // QOS ExactlyOnce means we can't give it away yet, we gotta do a handshake 
                // to make sure the broker knows we got it, and we know he knows we got it.
                
                // if we've already got it thats ok, it just means its being republished because
                // of a handshake breakdown, overwrite our existing one for the sake of it
                if (!receivedMessages.ContainsKey(pubMsg.VariableHeader.MessageIdentifier))
                {
                    receivedMessages[pubMsg.VariableHeader.MessageIdentifier] = pubMsg;
                }

                MqttPublishReceivedMessage pubRecv = new MqttPublishReceivedMessage()
                    .WithMessageIdentifier(pubMsg.VariableHeader.MessageIdentifier);
                connectionHandler.SendMessage(pubRecv);
                
                publishSuccess = true;
            }

            return publishSuccess;
        }

        /// <summary>
        /// Handles the publish complete, for messages that are undergoing Qos ExactlyOnce processing.
        /// </summary>
        /// <param name="msg">The MSG.</param>
        /// <returns>Boolean value indicating whether the message was successfull processed.</returns>
        private bool HandlePublishRelease(MqttMessage msg)
        {
            MqttPublishReleaseMessage pubRelMsg = (MqttPublishReleaseMessage)msg;
            bool publishSuccess = false;

            MqttPublishMessage pubMsg;
            receivedMessages.TryGetValue(pubRelMsg.VariableHeader.MessageIdentifier, out pubMsg);
            if (pubMsg != null)
            {
                receivedMessages.Remove(pubRelMsg.VariableHeader.MessageIdentifier);

                // send the message for processing to whoever is waiting.
                publishSuccess = publishMessageCallback(pubMsg);

                MqttPublishCompleteMessage compMsg = new MqttPublishCompleteMessage()
                    .WithMessageIdentifier(pubMsg.VariableHeader.MessageIdentifier);
                connectionHandler.SendMessage(compMsg);
            }
            else
            {
                // TODO: if we receive a publish release but haven't seen the original publish message, what do we do?!
            }

            return publishSuccess;
        }



        /*
         * 
         * The callbacks below are to deal with workflow when sending messages TO a broker.
         *
         */
        
        /// <summary>
        /// Handles a publish complete message received from a broker.
        /// </summary>
        /// <param name="msg"></param>
        /// <returns>true if the message flow completed successfully, otherwise false.</returns>
        private bool HandlePublishComplete(MqttMessage msg)
        {
            MqttPublishCompleteMessage compMsg = (MqttPublishCompleteMessage)msg;
            return publishedMessages.Remove(compMsg.VariableHeader.MessageIdentifier);
        }
        
        /// <summary>
        /// Handles publish received messages during processing of QOS level 2 (Exactly once) messages.
        /// </summary>
        /// <param name="msg">The publish received message</param>
        /// <returns>true or false, depending on the success of message processing.</returns>
        private bool HandlePublishReceived(MqttMessage msg)
        {
            MqttPublishReceivedMessage recvMsg = (MqttPublishReceivedMessage)msg;
            
            // if we've got a matching message, respond with a "ok release it for processing"
            if (publishedMessages.ContainsKey(recvMsg.VariableHeader.MessageIdentifier))
            {
                MqttPublishReleaseMessage relMsg = new MqttPublishReleaseMessage()
                    .WithMessageIdentifier(recvMsg.VariableHeader.MessageIdentifier);
                connectionHandler.SendMessage(relMsg);
            }

            return true;
        }

    }
}
