/*
Copyright (c) 2013, 2014 Paolo Patierno

All rights reserved. This program and the accompanying materials
are made available under the terms of the Eclipse Public License v1.0
and Eclipse Distribution License v1.0 which accompany this distribution. 

The Eclipse Public License is available at 
   http://www.eclipse.org/legal/epl-v10.html
and the Eclipse Distribution License is available at 
   http://www.eclipse.org/org/documents/edl-v10.php.

Contributors:
   Paolo Patierno - initial API and implementation and/or initial documentation
*/

using System;
using System.Net;
#if !(WINDOWS_APP || WINDOWS_PHONE_APP)
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
#endif
using System.Threading;
using System.Threading.Tasks;
using uPLibrary.Networking.M2Mqtt.Exceptions;
using uPLibrary.Networking.M2Mqtt.Messages;
using uPLibrary.Networking.M2Mqtt.Session;
using uPLibrary.Networking.M2Mqtt.Utility;
using uPLibrary.Networking.M2Mqtt.Internal;
// if .Net Micro Framework
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3)
using Microsoft.SPOT;
#if SSL
using Microsoft.SPOT.Net.Security;
#endif
// else other frameworks (.Net, .Net Compact, Mono, Windows Phone) 
#else
using System.Collections.Generic;
#if (SSL && !(WINDOWS_APP || WINDOWS_PHONE_APP))
using System.Security.Authentication;
using System.Net.Security;
#endif
#endif

#if (WINDOWS_APP || WINDOWS_PHONE_APP)
using Windows.Networking.Sockets;
#endif

using System.Collections;

// alias needed due to Microsoft.SPOT.Trace in .Net Micro Framework
// (it's ambiguos with uPLibrary.Networking.M2Mqtt.Utility.Trace)
using MqttUtility = uPLibrary.Networking.M2Mqtt.Utility;
using System.IO;

namespace uPLibrary.Networking.M2Mqtt
{
    /// <summary>
    /// MQTT Client
    /// </summary>
    public class MqttClient
    {
#if BROKER
        #region Constants ...

        // thread names
        private const string RECEIVE_THREAD_NAME = "ReceiveThread";
        private const string RECEIVE_EVENT_THREAD_NAME = "DispatchEventThread";
        private const string PROCESS_INFLIGHT_THREAD_NAME = "ProcessInflightThread";
        private const string KEEP_ALIVE_THREAD = "KeepAliveThread";

        #endregion
#endif

        /// <summary>
        /// Delagate that defines event handler for PUBLISH message received
        /// </summary>
        public delegate void MqttMsgPublishEventHandler(object sender, MqttMsgPublishEventArgs e);

        /// <summary>
        /// Delegate that defines event handler for published message
        /// </summary>
        public delegate void MqttMsgPublishedEventHandler(object sender, MqttMsgPublishedEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for subscribed topic
        /// </summary>
        public delegate void MqttMsgSubscribedEventHandler(object sender, MqttMsgSubscribedEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for unsubscribed topic
        /// </summary>
        public delegate void MqttMsgUnsubscribedEventHandler(object sender, MqttMsgUnsubscribedEventArgs e);

#if BROKER
        /// <summary>
        /// Delagate that defines event handler for SUBSCRIBE message received
        /// </summary>
        public delegate void MqttMsgSubscribeEventHandler(object sender, MqttMsgSubscribeEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for UNSUBSCRIBE message received
        /// </summary>
        public delegate void MqttMsgUnsubscribeEventHandler(object sender, MqttMsgUnsubscribeEventArgs e);

        /// <summary>
        /// Delagate that defines event handler for CONNECT message received
        /// </summary>
        public delegate void MqttMsgConnectEventHandler(object sender, MqttMsgConnectEventArgs e);

        /// <summary>
        /// Delegate that defines event handler for client disconnection (DISCONNECT message or not)
        /// </summary>
        public delegate void MqttMsgDisconnectEventHandler(object sender, EventArgs e);
#endif

        /// <summary>
        /// Delegate that defines event handler for cliet/peer disconnection
        /// </summary>
        public delegate void ConnectionClosedEventHandler(object sender, EventArgs e);

        // broker hostname (or ip address) and port
        private string brokerHostName;
        private int brokerPort;

        // running status of threads
        private bool isRunning;
        // event for raising received message event
        private AutoResetEvent receiveEventWaitHandle;

        // event for signaling synchronous receive
        AutoResetEvent syncEndReceiving;

        // event for publishing message - Katia's work
        AutoResetEvent publishHandle;

        // message received
        MqttMsgBase msgReceived;

        // exeption thrown during receiving
        Exception exReceiving;

        // keep alive period (in ms)
        private int keepAlivePeriod;
        // events for signaling on keep alive thread
        private AutoResetEvent keepAliveEvent;
        private AutoResetEvent keepAliveEventEnd;
        // last communication time in ticks
        private int lastCommTime;

        // event for PUBLISH message received
        public event MqttMsgPublishEventHandler MqttMsgPublishReceived;
        // event for published message
        public event MqttMsgPublishedEventHandler MqttMsgPublished;
        // event for subscribed topic
        public event MqttMsgSubscribedEventHandler MqttMsgSubscribed;
        // event for unsubscribed topic
        public event MqttMsgUnsubscribedEventHandler MqttMsgUnsubscribed;
#if BROKER
        // event for SUBSCRIBE message received
        public event MqttMsgSubscribeEventHandler MqttMsgSubscribeReceived;
        // event for USUBSCRIBE message received
        public event MqttMsgUnsubscribeEventHandler MqttMsgUnsubscribeReceived;
        // event for CONNECT message received
        public event MqttMsgConnectEventHandler MqttMsgConnected;
        // event for DISCONNECT message received
        public event MqttMsgDisconnectEventHandler MqttMsgDisconnected;
#endif

        // event for peer/client disconnection
        public event ConnectionClosedEventHandler ConnectionClosed;

        // channel to communicate over the network
        private IMqttNetworkChannel channel;
        // internal queue for publishing message -- Katia's work
        private Queue publishQueue;
        // session
        private MqttClientSession session;

        // reference to avoid access to singleton via property
        private MqttSettings settings;

        // current message identifier generated
        private ushort messageIdCounter = 0;

        // connection is closing due to peer
        private bool isConnectionClosing;

        /// <summary>
        /// Connection status between client and broker
        /// </summary>
        public bool IsConnected { get; private set; }

        /// <summary>
        /// Client identifier
        /// </summary>
        public string ClientId { get; private set; }

        /// <summary>
        /// Clean session flag
        /// </summary>
        public bool CleanSession { get; private set; }

        /// <summary>
        /// Will flag
        /// </summary>
        public bool WillFlag { get; private set; }

        /// <summary>
        /// Will QOS level
        /// </summary>
        public byte WillQosLevel { get; private set; }

        /// <summary>
        /// Will topic
        /// </summary>
        public string WillTopic { get; private set; }

        /// <summary>
        /// Will message
        /// </summary>
        public string WillMessage { get; private set; }

        /// <summary>
        /// MQTT protocol version
        /// </summary>
        public MqttProtocolVersion ProtocolVersion { get; set; }

#if BROKER
        /// <summary>
        /// MQTT Client Session
        /// </summary>
        public MqttClientSession Session
        {
            get { return this.session; }
            set { this.session = value; }
        }
#endif

        /// <summary>
        /// MQTT client settings
        /// </summary>
        public MqttSettings Settings
        {
            get { return this.settings; }
        }

#if !(WINDOWS_APP || WINDOWS_PHONE_APP) 
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerIpAddress">Broker IP address</param>
        [Obsolete("Use this ctor MqttClient(string brokerHostName) instead")]
        public MqttClient(IPAddress brokerIpAddress) :
            this(brokerIpAddress, MqttSettings.MQTT_BROKER_DEFAULT_PORT, false, null, null, MqttSslProtocols.None)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerIpAddress">Broker IP address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        [Obsolete("Use this ctor MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert) instead")]
        public MqttClient(IPAddress brokerIpAddress, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol)
        {
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            this.Init(brokerIpAddress.ToString(), brokerPort, secure, caCert, clientCert, sslProtocol, null, null);
#else
            this.Init(brokerIpAddress.ToString(), brokerPort, secure, caCert, clientCert, sslProtocol);
#endif
        }
#endif

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        public MqttClient(string brokerHostName) :
#if !(WINDOWS_APP || WINDOWS_PHONE_APP)
            this(brokerHostName, MqttSettings.MQTT_BROKER_DEFAULT_PORT, false, null, null, MqttSslProtocols.None)
#else
            this(brokerHostName, MqttSettings.MQTT_BROKER_DEFAULT_PORT, false, MqttSslProtocols.None)
#endif
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
#if !(WINDOWS_APP || WINDOWS_PHONE_APP)
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol)
#else
        public MqttClient(string brokerHostName, int brokerPort, bool secure, MqttSslProtocols sslProtocol)            
#endif
        {
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, null, null);
#elif (WINDOWS_APP || WINDOWS_PHONE_APP)
            this.Init(brokerHostName, brokerPort, secure, sslProtocol);
#else
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol);
#endif
        }


#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="userCertificateValidationCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback)
            : this(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, userCertificateValidationCallback, null)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="userCertificateValidationCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateSelectionCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback)
            : this(brokerHostName, brokerPort, secure, null, null, sslProtocol, userCertificateValidationCallback, userCertificateSelectionCallback)
        {
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
        /// <param name="userCertificateValidationCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateSelectionCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        public MqttClient(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback)
        {
            this.Init(brokerHostName, brokerPort, secure, caCert, clientCert, sslProtocol, userCertificateValidationCallback, userCertificateSelectionCallback);
        }
#endif

#if BROKER
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="channel">Network channel for communication</param>
        public MqttClient(IMqttNetworkChannel channel)
        {
            // set default MQTT protocol version (default is 3.1.1)
            this.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;

            this.channel = channel;

            // reference to MQTT settings
            this.settings = MqttSettings.Instance;

            // client not connected yet (CONNACK not send from client), some default values
            this.IsConnected = false;
            this.ClientId = null;
            this.CleanSession = true;

            this.keepAliveEvent = new AutoResetEvent(false);

            // queue for handling inflight messages (publishing and acknowledge)
            this.inflightWaitHandle = new AutoResetEvent(false);
            this.inflightQueue = new Queue();

            // queue for received message
            this.receiveEventWaitHandle = new AutoResetEvent(false);
            this.eventQueue = new Queue();
            this.internalQueue = new Queue();

            // session
            this.session = null;
        }
#endif

        /// <summary>
        /// MqttClient initialization
        /// </summary>
        /// <param name="brokerHostName">Broker Host Name or IP Address</param>
        /// <param name="brokerPort">Broker port</param>
        /// <param name="secure">>Using secure connection</param>
        /// <param name="caCert">CA certificate for secure connection</param>
        /// <param name="clientCert">Client certificate</param>
        /// <param name="sslProtocol">SSL/TLS protocol version</param>
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
        /// <param name="userCertificateSelectionCallback">A RemoteCertificateValidationCallback delegate responsible for validating the certificate supplied by the remote party</param>
        /// <param name="userCertificateValidationCallback">A LocalCertificateSelectionCallback delegate responsible for selecting the certificate used for authentication</param>
        private void Init(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol,
            RemoteCertificateValidationCallback userCertificateValidationCallback,
            LocalCertificateSelectionCallback userCertificateSelectionCallback)
#elif (WINDOWS_APP || WINDOWS_PHONE_APP)
        private void Init(string brokerHostName, int brokerPort, bool secure, MqttSslProtocols sslProtocol)
#else
        private void Init(string brokerHostName, int brokerPort, bool secure, X509Certificate caCert, X509Certificate clientCert, MqttSslProtocols sslProtocol)
#endif
        {
            // set default MQTT protocol version (default is 3.1.1)
            this.ProtocolVersion = MqttProtocolVersion.Version_3_1_1;
#if !SSL
            // check security parameters
            if (secure)
                throw new ArgumentException("Library compiled without SSL support");
#endif

            this.brokerHostName = brokerHostName;
            this.brokerPort = brokerPort;

            // reference to MQTT settings
            this.settings = MqttSettings.Instance;
            // set settings port based on secure connection or not
            if (!secure)
                this.settings.Port = this.brokerPort;
            else
                this.settings.SslPort = this.brokerPort;

            this.syncEndReceiving = new AutoResetEvent(false);
            this.keepAliveEvent = new AutoResetEvent(false);

            // queue for publishing message - Katia's work
            this.publishHandle = new AutoResetEvent(false);
            this.publishQueue = new Queue();

            // session
            this.session = null;

            // create network channel
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
            this.channel = new MqttNetworkChannel(this.brokerHostName, this.brokerPort, secure, caCert, clientCert, sslProtocol, userCertificateValidationCallback, userCertificateSelectionCallback);
#elif (WINDOWS_APP || WINDOWS_PHONE_APP)
            this.channel = new MqttNetworkChannel(this.brokerHostName, this.brokerPort, secure, sslProtocol);
#else
            this.channel = new MqttNetworkChannel(this.brokerHostName, this.brokerPort, secure, caCert, clientCert, sslProtocol);
#endif
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId)
        {
            return this.Connect(clientId, null, null, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, true, MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="cleanSession">Clean sessione flag</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            bool cleanSession)
        {
            return this.Connect(clientId, null, null, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, cleanSession, MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            string username,
            string password)
        {
            return this.Connect(clientId, username, password, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, true, MqttMsgConnect.KEEP_ALIVE_PERIOD_DEFAULT);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        /// <param name="cleanSession">Clean sessione flag</param>
        /// <param name="keepAlivePeriod">Keep alive period</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            string username,
            string password,
            bool cleanSession,
            ushort keepAlivePeriod)
        {
            return this.Connect(clientId, username, password, false, MqttMsgConnect.QOS_LEVEL_AT_MOST_ONCE, false, null, null, cleanSession, keepAlivePeriod);
        }

        /// <summary>
        /// Connect to broker
        /// </summary>
        /// <param name="clientId">Client identifier</param>
        /// <param name="username">Username</param>
        /// <param name="password">Password</param>
        /// <param name="willRetain">Will retain flag</param>
        /// <param name="willQosLevel">Will QOS level</param>
        /// <param name="willFlag">Will flag</param>
        /// <param name="willTopic">Will topic</param>
        /// <param name="willMessage">Will message</param>
        /// <param name="cleanSession">Clean sessione flag</param>
        /// <param name="keepAlivePeriod">Keep alive period</param>
        /// <returns>Return code of CONNACK message from broker</returns>
        public byte Connect(string clientId,
            string username,
            string password,
            bool willRetain,
            byte willQosLevel,
            bool willFlag,
            string willTopic,
            string willMessage,
            bool cleanSession,
            ushort keepAlivePeriod)
        {
            // create CONNECT message
            MqttMsgConnect connect = new MqttMsgConnect(clientId,
                username,
                password,
                willRetain,
                willQosLevel,
                willFlag,
                willTopic,
                willMessage,
                cleanSession,
                keepAlivePeriod,
                (byte)this.ProtocolVersion);

            try
            {
                // connect to the broker
                this.channel.Connect();
            }
            catch (Exception ex)
            {
                throw new MqttConnectionException("Exception connecting to the broker", ex);
            }

            this.lastCommTime = 0;
            this.isRunning = true;
            this.isConnectionClosing = false;
            // start thread for receiving messages from broker
            Fx.StartThread(this.ReceiveThread);

            MqttMsgConnack connack = (MqttMsgConnack)this.SendReceive(connect);
            // if connection accepted, start keep alive timer and 
            if (connack.ReturnCode == MqttMsgConnack.CONN_ACCEPTED)
            {
                // set all client properties
                this.ClientId = clientId;
                this.CleanSession = cleanSession;
                this.WillFlag = willFlag;
                this.WillTopic = willTopic;
                this.WillMessage = willMessage;
                this.WillQosLevel = willQosLevel;

                this.keepAlivePeriod = keepAlivePeriod * 1000; // convert in ms

                // restore previous session
                this.RestoreSession();

                // keep alive period equals zero means turning off keep alive mechanism
                if (this.keepAlivePeriod != 0)
                {
                    // start thread for sending keep alive message to the broker
                    Fx.StartThread(this.KeepAliveThread);
                }

                
                // start thread for handling publish messages queue to broker asynchronously - Katia's work
                Fx.StartThread(this.sendData);

                this.IsConnected = true;
            }
            return connack.ReturnCode;
        }

        /// <summary>
        /// Disconnect from broker
        /// </summary>
        public void Disconnect()
        {
            MqttMsgDisconnect disconnect = new MqttMsgDisconnect();
            this.Send(disconnect);

            // close client
            this.OnConnectionClosing();
        }

#if BROKER
        /// <summary>
        /// Open client communication
        /// </summary>
        public void Open()
        {
            this.isRunning = true;

            // start thread for receiving messages from client
            Fx.StartThread(this.ReceiveThread);

            // start thread for raising received message event from client
            Fx.StartThread(this.DispatchEventThread);

            // start thread for handling inflight messages queue to client asynchronously (publish and acknowledge)
            Fx.StartThread(this.ProcessInflightThread);   
        }
#endif

        /// <summary>
        /// Close client
        /// </summary>
#if BROKER
        public void Close()
#else
        private void Close()
#endif
        {
            // stop receiving thread
            this.isRunning = false;

            // wait end receive event thread
            if (this.receiveEventWaitHandle != null)
                this.receiveEventWaitHandle.Set();

            // wait end publish thread - Katia's work
            if (this.publishHandle != null)
                this.publishHandle.Set();
#if BROKER
            // unlock keep alive thread
            this.keepAliveEvent.Set();
#else
            // unlock keep alive thread and wait
            this.keepAliveEvent.Set();

            if (this.keepAliveEventEnd != null)
                this.keepAliveEventEnd.WaitOne();
#endif

            // unlock publish queue - Katia's work
            this.publishQueue.Clear();

            // close network channel
            this.channel.Close();

            this.IsConnected = false;
        }

        /// <summary>
        /// Execute ping to broker for keep alive
        /// </summary>
        /// <returns>PINGRESP message from broker</returns>
        private MqttMsgPingResp Ping()
        {
            MqttMsgPingReq pingreq = new MqttMsgPingReq();
            try
            {
                // broker must send PINGRESP within timeout equal to keep alive period
                return (MqttMsgPingResp)this.SendReceive(pingreq, this.keepAlivePeriod);
            }
            catch (Exception e)
            {
#if TRACE
                MqttUtility.Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", e.ToString());
#endif

                // client must close connection
                this.OnConnectionClosing();
                return null;
            }
        }

#if BROKER
        /// <summary>
        /// Send CONNACK message to the client (connection accepted or not)
        /// </summary>
        /// <param name="connect">CONNECT message with all client information</param>
        /// <param name="returnCode">Return code for CONNACK message</param>
        /// <param name="clientId">If not null, client id assigned by broker</param>
        /// <param name="sessionPresent">Session present on the broker</param>
        public void Connack(MqttMsgConnect connect, byte returnCode, string clientId, bool sessionPresent)
        {
            this.lastCommTime = 0;

            // create CONNACK message and ...
            MqttMsgConnack connack = new MqttMsgConnack();
            connack.ReturnCode = returnCode;
            // [v3.1.1] session present flag
            if (this.ProtocolVersion == MqttProtocolVersion.Version_3_1_1)
                connack.SessionPresent = sessionPresent;
            // ... send it to the client
            this.Send(connack);

            // connection accepted, start keep alive thread checking
            if (connack.ReturnCode == MqttMsgConnack.CONN_ACCEPTED)
            {
                // [v3.1.1] if client id isn't null, the CONNECT message has a cliend id with zero bytes length
                //          and broker assigned a unique identifier to the client
                this.ClientId = (clientId == null) ? connect.ClientId : clientId;
                this.CleanSession = connect.CleanSession;
                this.WillFlag = connect.WillFlag;
                this.WillTopic = connect.WillTopic;
                this.WillMessage = connect.WillMessage;
                this.WillQosLevel = connect.WillQosLevel;

                this.keepAlivePeriod = connect.KeepAlivePeriod * 1000; // convert in ms
                // broker has a tolerance of 1.5 specified keep alive period
                this.keepAlivePeriod += (this.keepAlivePeriod / 2);

                // start thread for checking keep alive period timeout
                Fx.StartThread(this.KeepAliveThread);

                this.isConnectionClosing = false;
                this.IsConnected = true;
            }
            // connection refused, close TCP/IP channel
            else
            {
                this.Close();
            }
        }

        /// <summary>
        /// Send SUBACK message to the client
        /// </summary>
        /// <param name="messageId">Message Id for the SUBSCRIBE message that is being acknowledged</param>
        /// <param name="grantedQosLevels">Granted QoS Levels</param>
        public void Suback(ushort messageId, byte[] grantedQosLevels)
        {
            MqttMsgSuback suback = new MqttMsgSuback();
            suback.MessageId = messageId;
            suback.GrantedQoSLevels = grantedQosLevels;

            this.Send(suback);
        }

        /// <summary>
        /// Send UNSUBACK message to the client
        /// </summary>
        /// <param name="messageId">Message Id for the UNSUBSCRIBE message that is being acknowledged</param>
        public void Unsuback(ushort messageId)
        {
            MqttMsgUnsuback unsuback = new MqttMsgUnsuback();
            unsuback.MessageId = messageId;

            this.Send(unsuback);
        }
#endif

        /// <summary>
        /// Subscribe for message topics
        /// </summary>
        /// <param name="topics">List of topics to subscribe</param>
        /// <param name="qosLevels">QOS levels related to topics</param>
        /// <returns>Message Id related to SUBSCRIBE message</returns>
        public ushort Subscribe(string[] topics, byte[] qosLevels)
        {
            MqttMsgSubscribe subscribe =
                new MqttMsgSubscribe(topics, qosLevels);
            subscribe.MessageId = this.GetMessageId();

            // enqueue subscribe request into the inflight queue
            this.EnqueueInflight(subscribe, MqttMsgFlow.ToPublish);

            return subscribe.MessageId;
        }

        /// <summary>
        /// Unsubscribe for message topics
        /// </summary>
        /// <param name="topics">List of topics to unsubscribe</param>
        /// <returns>Message Id in UNSUBACK message from broker</returns>
        public ushort Unsubscribe(string[] topics)
        {
            MqttMsgUnsubscribe unsubscribe =
                new MqttMsgUnsubscribe(topics);
            unsubscribe.MessageId = this.GetMessageId();

            // enqueue unsubscribe request into the inflight queue
            this.EnqueueInflight(unsubscribe, MqttMsgFlow.ToPublish);

            return unsubscribe.MessageId;
        }

        /// <summary>
        /// Publish a message asynchronously (QoS Level 0 and not retained)
        /// </summary>
        /// <param name="topic">Message topic</param>
        /// <param name="message">Message data (payload)</param>
        /// <returns>Message Id related to PUBLISH message</returns>
        public ushort Publish(string topic, byte[] message)
        {
            return this.Publish(topic, message, MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE, false);
        }

        /// <summary>
        /// Publish a message asynchronously
        /// </summary>
        /// <param name="topic">Message topic</param>
        /// <param name="message">Message data (payload)</param>
        /// <param name="qosLevel">QoS Level</param>
        /// <param name="retain">Retain flag</param>
        /// <returns>Message Id related to PUBLISH message</returns>
        public ushort Publish(string topic, byte[] message, byte qosLevel, bool retain)
        {
            MqttMsgPublish publish =
                    new MqttMsgPublish(topic, message, false, qosLevel, retain);
            publish.MessageId = this.GetMessageId();

            // enqueue message to publish into the inflight queue
            bool enqueue = this.EnqueueInflight(publish, MqttMsgFlow.ToPublish);

            // message enqueued
            if (enqueue)
            {
                return publish.MessageId;
            }
            // infligh queue full, message not enqueued
            else
                throw new MqttClientException(MqttClientErrorCode.InflightQueueFull);
        }

        /// <summary>
        /// Wrapper method for raising events
        /// </summary>
        /// <param name="internalEvent">Internal event</param>
        private void OnInternalEvent(InternalEvent internalEvent)
        {
        }

        /// <summary>
        /// Wrapper method for raising closing connection event
        /// </summary>
        private void OnConnectionClosing()
        {
            if (!this.isConnectionClosing)
            {
                this.isConnectionClosing = true;
            }
        }

        /// <summary>
        /// Wrapper method for raising PUBLISH message received event
        /// </summary>
        /// <param name="publish">PUBLISH message received</param>
        private void OnMqttMsgPublishReceived(MqttMsgPublish publish)
        {
            if (this.MqttMsgPublishReceived != null)
            {
                this.MqttMsgPublishReceived(this,
                    new MqttMsgPublishEventArgs(publish.Topic, publish.Message, publish.DupFlag, publish.QosLevel, publish.Retain));
            }
        }

        /// <summary>
        /// Wrapper method for raising published message event
        /// </summary>
        /// <param name="messageId">Message identifier for published message</param>
        /// <param name="isPublished">Publish flag</param>
        private void OnMqttMsgPublished(ushort messageId, bool isPublished)
        {
            if (this.MqttMsgPublished != null)
            {
                this.MqttMsgPublished(this,
                    new MqttMsgPublishedEventArgs(messageId, isPublished));
            }
        }

        /// <summary>
        /// Wrapper method for raising subscribed topic event
        /// </summary>
        /// <param name="suback">SUBACK message received</param>
        private void OnMqttMsgSubscribed(MqttMsgSuback suback)
        {
            if (this.MqttMsgSubscribed != null)
            {
                this.MqttMsgSubscribed(this,
                    new MqttMsgSubscribedEventArgs(suback.MessageId, suback.GrantedQoSLevels));
            }
        }

        /// <summary>
        /// Wrapper method for raising unsubscribed topic event
        /// </summary>
        /// <param name="messageId">Message identifier for unsubscribed topic</param>
        private void OnMqttMsgUnsubscribed(ushort messageId)
        {
            if (this.MqttMsgUnsubscribed != null)
            {
                this.MqttMsgUnsubscribed(this,
                    new MqttMsgUnsubscribedEventArgs(messageId));
            }
        }

#if BROKER
        /// <summary>
        /// Wrapper method for raising SUBSCRIBE message event
        /// </summary>
        /// <param name="messageId">Message identifier for subscribe topics request</param>
        /// <param name="topics">Topics requested to subscribe</param>
        /// <param name="qosLevels">List of QOS Levels requested</param>
        private void OnMqttMsgSubscribeReceived(ushort messageId, string[] topics, byte[] qosLevels)
        {
            if (this.MqttMsgSubscribeReceived != null)
            {
                this.MqttMsgSubscribeReceived(this,
                    new MqttMsgSubscribeEventArgs(messageId, topics, qosLevels));
            }
        }

        /// <summary>
        /// Wrapper method for raising UNSUBSCRIBE message event
        /// </summary>
        /// <param name="messageId">Message identifier for unsubscribe topics request</param>
        /// <param name="topics">Topics requested to unsubscribe</param>
        private void OnMqttMsgUnsubscribeReceived(ushort messageId, string[] topics)
        {
            if (this.MqttMsgUnsubscribeReceived != null)
            {
                this.MqttMsgUnsubscribeReceived(this,
                    new MqttMsgUnsubscribeEventArgs(messageId, topics));
            }
        }

        /// <summary>
        /// Wrapper method for raising CONNECT message event
        /// </summary>
        private void OnMqttMsgConnected(MqttMsgConnect connect)
        {
            if (this.MqttMsgConnected != null)
            {
                this.ProtocolVersion = (MqttProtocolVersion)connect.ProtocolVersion;
                this.MqttMsgConnected(this, new MqttMsgConnectEventArgs(connect));
            }
        }

        /// <summary>
        /// Wrapper method for raising DISCONNECT message event
        /// </summary>
        private void OnMqttMsgDisconnected()
        {
            if (this.MqttMsgDisconnected != null)
            {
                this.MqttMsgDisconnected(this, EventArgs.Empty);
            }
        }
#endif

        /// <summary>
        /// Wrapper method for peer/client disconnection
        /// </summary>
        private void OnConnectionClosed()
        {
            if (this.ConnectionClosed != null)
            {
                this.ConnectionClosed(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Send a message
        /// </summary>
        /// <param name="msgBytes">Message bytes</param>
        private void Send(byte[] msgBytes)
        {
            try
            {
                // send message
                this.channel.Send(msgBytes);

#if !BROKER
                // update last message sent ticks
                this.lastCommTime = Environment.TickCount;
#endif
            }
            catch (Exception e)
            {
#if TRACE
                MqttUtility.Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", e.ToString());
#endif

                throw new MqttCommunicationException(e);
            }
        }

        /// <summary>
        /// Send a message
        /// </summary>
        /// <param name="msg">Message</param>
        private void Send(MqttMsgBase msg)
        {
#if TRACE
            MqttUtility.Trace.WriteLine(TraceLevel.Frame, "SEND {0}", msg);
#endif
            this.Send(msg.GetBytes((byte)this.ProtocolVersion));
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msgBytes">Message bytes</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(byte[] msgBytes)
        {
            return this.SendReceive(msgBytes, MqttSettings.MQTT_DEFAULT_TIMEOUT);
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msgBytes">Message bytes</param>
        /// <param name="timeout">Timeout for receiving answer</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(byte[] msgBytes, int timeout)
        {
            // reset handle before sending
            this.syncEndReceiving.Reset();
            try
            {
                // send message
                this.channel.Send(msgBytes);

                // update last message sent ticks
                this.lastCommTime = Environment.TickCount;
            }
            catch (Exception e)
            {
#if !(MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK || WINDOWS_APP || WINDOWS_PHONE_APP)
                if (typeof(SocketException) == e.GetType())
                {
                    // connection reset by broker
                    if (((SocketException)e).SocketErrorCode == SocketError.ConnectionReset)
                        this.IsConnected = false;
                }
#endif
#if TRACE
                MqttUtility.Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", e.ToString());
#endif

                throw new MqttCommunicationException(e);
            }

            if (this.msgReceived != null)
                return this.msgReceived;
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
            // wait for answer from broker
            if (this.syncEndReceiving.WaitOne(timeout, false))
#else
            // wait for answer from broker
            if (this.syncEndReceiving.WaitOne(timeout))
#endif
            {
                // message received without exception
                if (this.exReceiving == null)
                    return this.msgReceived;
                // receiving thread catched exception
                else
                    throw this.exReceiving;
            }
            else
            {
                // throw timeout exception
                throw new MqttCommunicationException();
            }
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msg">Message</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(MqttMsgBase msg)
        {
            return this.SendReceive(msg, MqttSettings.MQTT_DEFAULT_TIMEOUT);
        }

        /// <summary>
        /// Send a message to the broker and wait answer
        /// </summary>
        /// <param name="msg">Message</param>
        /// <param name="timeout">Timeout for receiving answer</param>
        /// <returns>MQTT message response</returns>
        private MqttMsgBase SendReceive(MqttMsgBase msg, int timeout)
        {
#if TRACE
            MqttUtility.Trace.WriteLine(TraceLevel.Frame, "SEND {0}", msg);
#endif
            return this.SendReceive(msg.GetBytes((byte)this.ProtocolVersion), timeout);
        }

        /// Katia's work - Send message
        /// <summary>
        /// Enqueue a message into the publish queue
        /// </summary>
        /// <param name="msg">Message to enqueue</param>
        /// <param name="flow">Message flow (publish)</param>
        /// <returns>Message enqueued or not</returns>
        private void sendData()
        {
            MqttMsgContext msgContext = null;
            DateTime startDate;
            try
            {
                while (this.isRunning)
                {
                    if (this.isRunning)
                    {
                        lock (this.publishQueue)
                        {
                            int count = this.publishQueue.Count;
                            startDate = DateTime.Now; // save beginning time.
                            while (count > 0)
                            {
                                // Compare time.
                                DateTime currentDate = DateTime.Now;
                                long elapsedTicks = currentDate.Ticks - startDate.Ticks;
                                TimeSpan elapsedSpan = new TimeSpan(elapsedTicks);

                                if (elapsedSpan.TotalSeconds > 1)
                                {
                                    // clear publish queue
                                    publishQueue.Clear();
                                    count = 0;
                                    startDate = DateTime.Now;
                                    break;
                                }
                                count--;
                                if (this.isRunning)
                                {
                                    msgContext = (MqttMsgContext)this.publishQueue.Dequeue();
                                    if (msgContext == null)
                                        continue;
                                    // get inflight message
                                    MqttMsgBase msgInflight = (MqttMsgBase)msgContext.Message;

                                    switch (msgContext.State)
                                    {
                                        case MqttMsgState.QueuedQos0:

                                            // QoS 0, PUBLISH message to send to broker, no state change, no acknowledge
                                            if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                            {
                                                this.Send(msgInflight);
                                            }
#if TRACE
                                            MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "processed {0}", msgInflight);
#endif
                                            break;

                                        case MqttMsgState.QueuedQos1:
                                            // QoS 1, PUBLISH or SUBSCRIBE/UNSUBSCRIBE message to send to broker, state change to wait PUBACK or SUBACK/UNSUBACK
                                            if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                            {
                                                msgContext.Timestamp = Environment.TickCount;
                                                msgContext.Attempt++;

                                                if (msgInflight.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE)
                                                {
                                                    // PUBLISH message to send, wait for PUBACK
                                                    msgContext.State = MqttMsgState.WaitForPuback;
                                                    // retry ? set dup flag [v3.1.1] only for PUBLISH message
                                                    if (msgContext.Attempt > 1)
                                                        msgInflight.DupFlag = true;
                                                }

                                                this.Send(msgInflight);
                                            }
                                            break;

                                        case MqttMsgState.QueuedQos2:

                                            // QoS 2, PUBLISH message to send to broker, state change to wait PUBREC
                                            if (msgContext.Flow == MqttMsgFlow.ToPublish)
                                            {
                                                msgContext.Timestamp = Environment.TickCount;
                                                msgContext.Attempt++;
                                                msgContext.State = MqttMsgState.WaitForPubrec;
                                                // retry ? set dup flag
                                                if (msgContext.Attempt > 1)
                                                    msgInflight.DupFlag = true;

                                                this.Send(msgInflight);

                                            }
                                            break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (MqttCommunicationException e)
            {
                // possible exception on Send, I need to re-enqueue not sent message
                if (msgContext != null)
                    // re-enqueue message
                    this.publishQueue.Enqueue(msgContext);

#if TRACE
                MqttUtility.Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", e.ToString());
#endif

                // raise disconnection client event
                this.OnConnectionClosing();
            }
        }
        private bool EnqueueInflight(MqttMsgBase msg, MqttMsgFlow flow)
        {
            // enqueue is needed (or not)
            bool enqueue = true;

            // if it is a PUBLISH message with QoS Level 2 - Katia's work
            if ((msg.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                (msg.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE))
            {

                lock (this.publishQueue)
                {
                    // if it is a PUBLISH message already received (it is in the inflight queue), the publisher
                    // re-sent it because it didn't received the PUBREC. In this case, we have to re-send PUBREC

                    // NOTE : I need to find on message id and flow because the broker could be publish/received
                    //        to/from client and message id could be the same (one tracked by broker and the other by client)
                    MqttMsgContextFinder msgCtxFinder = new MqttMsgContextFinder(msg.MessageId, MqttMsgFlow.ToAcknowledge);
                    MqttMsgContext msgCtx = (MqttMsgContext)this.publishQueue.Get(msgCtxFinder.Find);

                    // the PUBLISH message is alredy in the inflight queue, we don't need to re-enqueue but we need
                    // to change state to re-send PUBREC
                    if (msgCtx != null)
                    {
                        msgCtx.State = MqttMsgState.QueuedQos2;
                        msgCtx.Flow = MqttMsgFlow.ToAcknowledge;
                        enqueue = false;
                    }
                }
            }

            if (enqueue)
            {
                // set a default state
                MqttMsgState state = MqttMsgState.QueuedQos0;

                // based on QoS level, the messages flow between broker and client changes
                switch (msg.QosLevel)
                {
                    // QoS Level 0
                    case MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE:

                        state = MqttMsgState.QueuedQos0;
                        break;

                    // QoS Level 1
                    case MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE:

                        state = MqttMsgState.QueuedQos1;
                        break;

                    // QoS Level 2
                    case MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE:

                        state = MqttMsgState.QueuedQos2;
                        break;
                }

                // [v3.1.1] SUBSCRIBE and UNSUBSCRIBE aren't "officially" QOS = 1
                //          so QueuedQos1 state isn't valid for them
                if (msg.Type == MqttMsgBase.MQTT_MSG_SUBSCRIBE_TYPE)
                    state = MqttMsgState.SendSubscribe;
                else if (msg.Type == MqttMsgBase.MQTT_MSG_UNSUBSCRIBE_TYPE)
                    state = MqttMsgState.SendUnsubscribe;

                // queue message context
                MqttMsgContext msgContext = new MqttMsgContext()
                {
                    Message = msg,
                    State = state,
                    Flow = flow,
                    Attempt = 0
                };

                lock (this.publishQueue)
                {
                    // check number of messages inside inflight queue 
                    enqueue = (this.publishQueue.Count < this.settings.publishQueueSize);

                    if (enqueue)
                    {
                        // enqueue message and unlock send thread
                        this.publishQueue.Enqueue(msgContext);

#if TRACE
                        MqttUtility.Trace.WriteLine(TraceLevel.Queuing, "enqueued {0}", msg);
#endif

                        // PUBLISH message
                        if (msg.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE)
                        {
                            // to publish and QoS level 1 or 2
                            if ((msgContext.Flow == MqttMsgFlow.ToPublish) &&
                                ((msg.QosLevel == MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE) ||
                                 (msg.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE)))
                            {
                                if (this.session != null)
                                    this.session.InflightMessages.Add(msgContext.Key, msgContext);
                            }
                            // to acknowledge and QoS level 2
                            else if ((msgContext.Flow == MqttMsgFlow.ToAcknowledge) &&
                                     (msg.QosLevel == MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE))
                            {
                                if (this.session != null)
                                    this.session.InflightMessages.Add(msgContext.Key, msgContext);
                            }
                        }
                    }

                }
            }

            this.publishHandle.Set();

            return enqueue;
        }

        /// <summary>
        /// Enqueue a message into the internal queue
        /// </summary>
        /// <param name="msg">Message to enqueue</param>
        private void EnqueueInternal(MqttMsgBase msg)
        {
            // enqueue is needed (or not)
            bool enqueue = true;

            // if it is a PUBREL message (for QoS Level 2)
            if (msg.Type == MqttMsgBase.MQTT_MSG_PUBREL_TYPE)
            {
                
            }
            // if it is a PUBCOMP message (for QoS Level 2)
            else if (msg.Type == MqttMsgBase.MQTT_MSG_PUBCOMP_TYPE)
            {
                
            }
            // if it is a PUBREC message (for QoS Level 2)
            else if (msg.Type == MqttMsgBase.MQTT_MSG_PUBREC_TYPE)
            {
            }

            if (enqueue)
            {
            }
        }

        /// <summary>
        /// Thread for receiving messages
        /// </summary>
        private void ReceiveThread()
        {
            int readBytes = 0;
            byte[] fixedHeaderFirstByte = new byte[1];
            byte msgType;

            while (this.isRunning)
            {
                try
                {
                    // read first byte (fixed header)
                    readBytes = this.channel.Receive(fixedHeaderFirstByte);

                    if (readBytes > 0)
                    {
#if BROKER
                        // update last message received ticks
                        this.lastCommTime = Environment.TickCount;
#endif

                        // extract message type from received byte
                        msgType = (byte)((fixedHeaderFirstByte[0] & MqttMsgBase.MSG_TYPE_MASK) >> MqttMsgBase.MSG_TYPE_OFFSET);

                        switch (msgType)
                        {
                            // CONNECT message received
                            case MqttMsgBase.MQTT_MSG_CONNECT_TYPE:

#if BROKER
                                MqttMsgConnect connect = MqttMsgConnect.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", connect);
#endif

                                // raise message received event
                                this.OnInternalEvent(new MsgInternalEvent(connect));
                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            // CONNACK message received
                            case MqttMsgBase.MQTT_MSG_CONNACK_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                this.msgReceived = MqttMsgConnack.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", this.msgReceived);
#endif
                                this.syncEndReceiving.Set();
                                break;
#endif

                            // PINGREQ message received
                            case MqttMsgBase.MQTT_MSG_PINGREQ_TYPE:

#if BROKER
                                this.msgReceived = MqttMsgPingReq.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", this.msgReceived);
#endif

                                MqttMsgPingResp pingresp = new MqttMsgPingResp();
                                this.Send(pingresp);

                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            // PINGRESP message received
                            case MqttMsgBase.MQTT_MSG_PINGRESP_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                this.msgReceived = MqttMsgPingResp.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", this.msgReceived);
#endif
                                this.syncEndReceiving.Set();
                                break;
#endif
                            // PUBREC message received
                            case MqttMsgBase.MQTT_MSG_PUBREC_TYPE:

                                // enqueue PUBREC message received (for QoS Level 2) into the internal queue
                                MqttMsgPubrec pubrec = MqttMsgPubrec.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", pubrec);
#endif

                                // enqueue PUBREC message into the internal queue
                                this.EnqueueInternal(pubrec);

                                break;

                            // PUBREL message received
                            case MqttMsgBase.MQTT_MSG_PUBREL_TYPE:

                                // enqueue PUBREL message received (for QoS Level 2) into the internal queue
                                MqttMsgPubrel pubrel = MqttMsgPubrel.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", pubrel);
#endif

                                // enqueue PUBREL message into the internal queue
                                this.EnqueueInternal(pubrel);

                                break;

                            // PUBCOMP message received
                            case MqttMsgBase.MQTT_MSG_PUBCOMP_TYPE:

                                // enqueue PUBCOMP message received (for QoS Level 2) into the internal queue
                                MqttMsgPubcomp pubcomp = MqttMsgPubcomp.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", pubcomp);
#endif

                                // enqueue PUBCOMP message into the internal queue
                                this.EnqueueInternal(pubcomp);

                                break;

                            // UNSUBACK message received
                            case MqttMsgBase.MQTT_MSG_UNSUBACK_TYPE:

#if BROKER
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#else
                                // enqueue UNSUBACK message received (for QoS Level 1) into the internal queue
                                MqttMsgUnsuback unsuback = MqttMsgUnsuback.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                MqttUtility.Trace.WriteLine(TraceLevel.Frame, "RECV {0}", unsuback);
#endif

                                // enqueue UNSUBACK message into the internal queue
                                this.EnqueueInternal(unsuback);

                                break;
#endif

                            // DISCONNECT message received
                            case MqttMsgDisconnect.MQTT_MSG_DISCONNECT_TYPE:

#if BROKER
                                MqttMsgDisconnect disconnect = MqttMsgDisconnect.Parse(fixedHeaderFirstByte[0], (byte)this.ProtocolVersion, this.channel);
#if TRACE
                                Trace.WriteLine(TraceLevel.Frame, "RECV {0}", disconnect);
#endif

                                // raise message received event
                                this.OnInternalEvent(new MsgInternalEvent(disconnect));

                                break;
#else
                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
#endif

                            default:

                                throw new MqttClientException(MqttClientErrorCode.WrongBrokerMessage);
                        }

                        this.exReceiving = null;
                    }
                    // zero bytes read, peer gracefully closed socket
                    else
                    {
                        // wake up thread that will notify connection is closing
                        this.OnConnectionClosing();
                    }
                }
                catch (Exception e)
                {
#if TRACE
                    MqttUtility.Trace.WriteLine(TraceLevel.Error, "Exception occurred: {0}", e.ToString());
#endif
                    this.exReceiving = new MqttCommunicationException(e);

                    bool close = false;
                    if (e.GetType() == typeof(MqttClientException))
                    {
                        // [v3.1.1] scenarios the receiver MUST close the network connection
                        MqttClientException ex = e as MqttClientException;
                        close = ((ex.ErrorCode == MqttClientErrorCode.InvalidFlagBits) ||
                                (ex.ErrorCode == MqttClientErrorCode.InvalidProtocolName) ||
                                (ex.ErrorCode == MqttClientErrorCode.InvalidConnectFlags));
                    }
#if !(WINDOWS_APP || WINDOWS_PHONE_APP)
                    else if ((e.GetType() == typeof(IOException)) || (e.GetType() == typeof(SocketException)) ||
                             ((e.InnerException != null) && (e.InnerException.GetType() == typeof(SocketException)))) // added for SSL/TLS incoming connection that use SslStream that wraps SocketException
                    {
                        close = true;
                    }
#endif

                    if (close)
                    {
                        // wake up thread that will notify connection is closing
                        this.OnConnectionClosing();
                    }
                }
            }
        }

        /// <summary>
        /// Thread for handling keep alive message
        /// </summary>
        private void KeepAliveThread()
        {
            int delta = 0;
            int wait = this.keepAlivePeriod;

            // create event to signal that current thread is end
            this.keepAliveEventEnd = new AutoResetEvent(false);

            while (this.isRunning)
            {
#if (MF_FRAMEWORK_VERSION_V4_2 || MF_FRAMEWORK_VERSION_V4_3 || COMPACT_FRAMEWORK)
                // waiting...
                this.keepAliveEvent.WaitOne(wait, false);
#else
                // waiting...
                this.keepAliveEvent.WaitOne(wait);
#endif

                if (this.isRunning)
                {
                    delta = Environment.TickCount - this.lastCommTime;

                    // if timeout exceeded ...
                    if (delta >= this.keepAlivePeriod)
                    {
#if BROKER
                        // client must close connection
                        this.OnConnectionClosing();
#else
                        // ... send keep alive
                        this.Ping();
                        wait = this.keepAlivePeriod;
#endif
                    }
                    else
                    {
                        // update waiting time
                        wait = this.keepAlivePeriod - delta;
                    }
                }
            }

            // signal thread end
            this.keepAliveEventEnd.Set();
        }

        /// <summary>
        /// Thread for raising event
        /// </summary>
        private void DispatchEventThread()
        {
            while (this.isRunning)
            {

               
            }
        }

        /// <summary>
        /// Process inflight messages queue
        /// </summary>
        private void ProcessInflightThread()
        {
           
        }

        /// <summary>
        /// Restore session
        /// </summary>
        private void RestoreSession()
        {
            // if not clean session
            if (!this.CleanSession)
            {
                // there is a previous session
                if (this.session != null)
                {
                    // unlock process publish queue - Katia's work
                    this.publishHandle.Set();
                }
                else
                {
                    // create new session
                    this.session = new MqttClientSession(this.ClientId);
                }
            }
            // clean any previous session
            else
            {
                if (this.session != null)
                    this.session.Clear();
            }
        }

#if BROKER

        /// <summary>
        /// Load a given session
        /// </summary>
        /// <param name="session">MQTT Client session to load</param>
        public void LoadSession(MqttClientSession session)
        {
            // if not clean session
            if (!this.CleanSession)
            {
                // set the session ...
                this.session = session;
                // ... and restore it
                this.RestoreSession();
            }
        }
#endif

        /// <summary>
        /// Generate the next message identifier
        /// </summary>
        /// <returns>Message identifier</returns>
        private ushort GetMessageId()
        {
            // if 0 or max UInt16, it becomes 1 (first valid messageId)
            this.messageIdCounter = ((this.messageIdCounter % UInt16.MaxValue) != 0) ? (ushort)(this.messageIdCounter + 1) : (ushort)1;
            return this.messageIdCounter;
        }

        /// <summary>
        /// Finder class for PUBLISH message inside a queue
        /// </summary>
        internal class MqttMsgContextFinder
        {
            // PUBLISH message id
            internal ushort MessageId { get; set; }
            // message flow into inflight queue
            internal MqttMsgFlow Flow { get; set; }

            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="messageId">Message Id</param>
            /// <param name="flow">Message flow inside inflight queue</param>
            internal MqttMsgContextFinder(ushort messageId, MqttMsgFlow flow)
            {
                this.MessageId = messageId;
                this.Flow = flow;
            }

            internal bool Find(object item)
            {
                MqttMsgContext msgCtx = (MqttMsgContext)item;
                return ((msgCtx.Message.Type == MqttMsgBase.MQTT_MSG_PUBLISH_TYPE) &&
                        (msgCtx.Message.MessageId == this.MessageId) &&
                        msgCtx.Flow == this.Flow);

            }
        }
    }

    /// <summary>
    /// MQTT protocol version
    /// </summary>
    public enum MqttProtocolVersion
    {
        Version_3_1 = MqttMsgConnect.PROTOCOL_VERSION_V3_1,
        Version_3_1_1 = MqttMsgConnect.PROTOCOL_VERSION_V3_1_1
    }
}
