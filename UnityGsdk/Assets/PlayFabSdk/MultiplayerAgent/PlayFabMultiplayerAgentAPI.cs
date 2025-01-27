﻿#if ENABLE_PLAYFABSERVER_API
namespace PlayFab
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using Internal;
    using MultiplayerAgent.Model;
    using UnityEngine;

#pragma warning disable 414
    public class PlayFabMultiplayerAgentAPI
    {
        // These two keys are only available after allocation (once readyForPlayers returns true)
        public const string SessionCookieKey = "sessionCookie";
        public const string SessionIdKey = "sessionId";

        public const string HeartbeatEndpointKey = "heartbeatEndpoint";
        public const string ServerIdKey = "serverId";
        public const string LogFolderKey = "logFolder";
        public const string SharedContentFolderKey = "sharedContentFolder";
        public const string CertificateFolderKey = "certificateFolder";
        public const string TitleIdKey = "titleId";
        public const string BuildIdKey = "buildId";
        public const string RegionKey = "region";
        public const string VmIdKey = "vmId";
        public const string PublicIpV4AddressKey = "publicIpV4Address";
        public const string FullyQualifiedDomainNameKey = "fullyQualifiedDomainName";

        public delegate void OnAgentCommunicationErrorEvent(string error);

        public delegate void OnMaintenanceEvent(DateTime? NextScheduledMaintenanceUtc);

        public delegate void OnServerActiveEvent();

        public delegate void OnShutdownEventk();

        private const string GsdkConfigFileEnvVarKey = "GSDK_CONFIG_FILE";

        private static string _baseUrl = string.Empty;

        private static GSDKConfiguration _gsdkconfig;

        private static IDictionary<string, string> _configMap;

        private static ISerializerPlugin _jsonWrapper;

        public static SessionConfig SessionConfig = new SessionConfig();
        public static HeartbeatRequest CurrentState = new HeartbeatRequest();
        public static ErrorStates CurrentErrorState = ErrorStates.Ok;
        public static bool IsProcessing;
        public static bool IsDebugging = false;
        public static event OnShutdownEventk OnShutDownCallback;
        public static event OnMaintenanceEvent OnMaintenanceCallback;
        public static event OnAgentCommunicationErrorEvent OnAgentErrorCallback;
        public static event OnServerActiveEvent OnServerActiveCallback;


        public static void Start()
        {
            _jsonWrapper = PluginManager.GetPlugin<ISerializerPlugin>(PluginContract.PlayFab_Serializer);

            string fileName = Environment.GetEnvironmentVariable(GsdkConfigFileEnvVarKey);
            if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
            {
                _gsdkconfig = _jsonWrapper.DeserializeObject<GSDKConfiguration>(File.ReadAllText(fileName));
            }
            else
            {
                Debug.LogError(string.Format("Environment variable {0} not defined", GsdkConfigFileEnvVarKey));
                Application.Quit();
            }

            _baseUrl = string.Format("http://{0}/v1/sessionHosts/{1}/heartbeats", _gsdkconfig.HeartbeatEndpoint, _gsdkconfig.SessionHostId);
            CurrentState.CurrentGameState = GameState.Initializing;
            CurrentErrorState = ErrorStates.Ok;
            CurrentState.CurrentPlayers = new List<ConnectedPlayer>();
            if (_configMap == null)
            {
                _configMap = CreateConfigMap(_gsdkconfig);
            }
            if (IsDebugging)
            {
                Debug.Log(_baseUrl);
                Debug.Log(_gsdkconfig.SessionHostId);
                Debug.Log(_gsdkconfig.LogFolder);
            }

            //Create an agent that can talk on the main-tread and pull on an interval.
            //This is a unity thing, need an object in the scene.
            GameObject agentView = new GameObject("PlayFabAgentView");
            agentView.AddComponent<PlayFabMultiplayerAgentView>();
        }

        private static IDictionary<string, string> CreateConfigMap(GSDKConfiguration localConfig)
        {
            var finalConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> certEntry in localConfig.GameCertificates)
            {
                finalConfig[certEntry.Key] = certEntry.Value;
            }

            foreach (KeyValuePair<string, string> metadata in localConfig.BuildMetadata)
            {
                finalConfig[metadata.Key] = metadata.Value;
            }

            foreach (KeyValuePair<string, string> port in localConfig.GamePorts)
            {
                finalConfig[port.Key] = port.Value;
            }

            finalConfig[HeartbeatEndpointKey] = localConfig.HeartbeatEndpoint;
            finalConfig[ServerIdKey] = localConfig.SessionHostId;
            finalConfig[VmIdKey] = localConfig.VmId;
            finalConfig[LogFolderKey] = localConfig.LogFolder;
            finalConfig[SharedContentFolderKey] = localConfig.SharedContentFolder;
            finalConfig[CertificateFolderKey] = localConfig.CertificateFolder;
            finalConfig[TitleIdKey] = localConfig.TitleId;
            finalConfig[BuildIdKey] = localConfig.BuildId;
            finalConfig[RegionKey] = localConfig.Region;
            finalConfig[PublicIpV4AddressKey] = localConfig.PublicIpV4Address;
            finalConfig[FullyQualifiedDomainNameKey] = localConfig.FullyQualifiedDomainName;

            return finalConfig;
        }

        public static void ReadyForPlayers()
        {
            CurrentState.CurrentGameState = GameState.StandingBy;
        }

        public static GameServerConnectionInfo GetGameServerConnectionInfo()
        {
            return _gsdkconfig.GameServerConnectionInfo;
        }

        public static void UpdateConnectedPlayers(IList<ConnectedPlayer> currentlyConnectedPlayers)
        {
            CurrentState.CurrentPlayers = currentlyConnectedPlayers;
        }

        public static IDictionary<string, string> GetConfigSettings()
        {
            return new Dictionary<string, string>(_configMap, StringComparer.OrdinalIgnoreCase);
        }

        public static IList<string> GetInitialPlayers()
        {
            return new List<string>(SessionConfig.InitialPlayers);
        }

        internal static void SendHeartBeatRequest()
        {
            string payload = _jsonWrapper.SerializeObject(CurrentState);
            if (string.IsNullOrEmpty(payload))
            {
                return;
            }

            byte[] payloadBytes = Encoding.ASCII.GetBytes(payload);

            if (IsDebugging)
            {
                Debug.Log(payload);
            }

            PlayFabHttp.SimplePostCall(_baseUrl, payloadBytes, success =>
            {
                string json = Encoding.UTF8.GetString(success);
                Debug.Log(json);
                if (string.IsNullOrEmpty(json))
                {
                    return;
                }

                HeartbeatResponse hb = _jsonWrapper.DeserializeObject<HeartbeatResponse>(json);
                if (hb != null)
                {
                    ProcessAgentResponse(hb);
                }

                CurrentErrorState = ErrorStates.Ok;
                IsProcessing = false;
            }, error =>
            {
                Guid guid = Guid.NewGuid();
                Debug.LogFormat("CurrentError: {0} - {1}", error, guid.ToString());
                //Exponential backoff for 30 minutes for retries.
                switch (CurrentErrorState)
                {
                    case ErrorStates.Ok:
                        CurrentErrorState = ErrorStates.Retry30S;
                        if (IsDebugging)
                        {
                            Debug.Log("Retrying heartbeat in 30s");
                        }

                        break;
                    case ErrorStates.Retry30S:
                        CurrentErrorState = ErrorStates.Retry5M;
                        if (IsDebugging)
                        {
                            Debug.Log("Retrying heartbeat in 5m");
                        }

                        break;
                    case ErrorStates.Retry5M:
                        CurrentErrorState = ErrorStates.Retry10M;
                        if (IsDebugging)
                        {
                            Debug.Log("Retrying heartbeat in 10m");
                        }

                        break;
                    case ErrorStates.Retry10M:
                        CurrentErrorState = ErrorStates.Retry15M;
                        if (IsDebugging)
                        {
                            Debug.Log("Retrying heartbeat in 15m");
                        }

                        break;
                    case ErrorStates.Retry15M:
                        CurrentErrorState = ErrorStates.Cancelled;
                        if (IsDebugging)
                        {
                            Debug.Log("Agent reconnection cannot be established - cancelling");
                        }

                        break;
                }

                if (OnAgentErrorCallback != null)
                {
                    OnAgentErrorCallback.Invoke(error);
                }

                IsProcessing = false;
            });
        }

        private static void ProcessAgentResponse(HeartbeatResponse heartBeat)
        {
            SessionConfig.CopyNonNullFields(heartBeat.SessionConfig);

            if (!string.IsNullOrEmpty(heartBeat.NextScheduledMaintenanceUtc))
            {
                DateTime scheduledMaintDate;

                if (DateTime.TryParse(
                    heartBeat.NextScheduledMaintenanceUtc,
                    null,
                    DateTimeStyles.RoundtripKind,
                    out scheduledMaintDate))
                {
                    if (OnMaintenanceCallback != null)
                    {
                        OnMaintenanceCallback.Invoke(scheduledMaintDate);
                    }
                }
            }

            switch (heartBeat.Operation)
            {
                case GameOperation.Continue:
                    //No Action Required.
                    break;
                case GameOperation.Active:
                    //Transition Server State to Active.
                    CurrentState.CurrentGameState = GameState.Active;
                    _configMap.Add(SessionIdKey, heartBeat.SessionConfig.SessionId);
                    _configMap.Add(SessionCookieKey, heartBeat.SessionConfig.SessionCookie);

                    if (OnServerActiveCallback != null)
                    {
                        OnServerActiveCallback.Invoke();
                    }

                    break;
                case GameOperation.Terminate:
                    if (CurrentState.CurrentGameState == GameState.Terminated)
                    {
                        break;
                    }

                    //Transition Server to a Termination state.
                    CurrentState.CurrentGameState = GameState.Terminating;
                    if (OnShutDownCallback != null)
                    {
                        OnShutDownCallback.Invoke();
                    }

                    break;
                default:
                    Debug.LogWarning("Unknown operation received" + heartBeat.Operation);
                    break;
            }

            if (IsDebugging)
            {
                Debug.LogFormat("Operation: {0}, Maintenance:{1}, State: {2}", heartBeat.Operation, heartBeat.NextScheduledMaintenanceUtc,
                    CurrentState.CurrentGameState);
            }
        }
    }
}

#endif