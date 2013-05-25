/*
 * Copyright (c) Contributors, http://aurora-sim.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using Aurora.Framework;
using Aurora.Modules.Chat;
using Aurora.Framework.PresenceInfo;
using GridRegion = Aurora.Framework.Services.GridRegion;
using Aurora.Framework.Modules;
using Aurora.Framework.SceneInfo;
using Aurora.Framework.Servers;
using Aurora.Framework.ClientInterfaces;
using Aurora.Framework.ConsoleFramework;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    public class ConciergeModule : AuroraChatModule, INonSharedRegionModule
    {
        private const int DEBUG_CHANNEL = 2147483647;

        private IScene m_scene;
        private bool m_replacingChatModule = false;

        private IConfig m_config;
        
        private string m_whoami = "conferencier";
        private Regex m_regions = null;
        private string m_welcomes = null;
        private int m_conciergeChannel = 42;
        private string m_announceEntering = "{0} enters {1} (now {2} visitors in this region)";
        private string m_announceLeaving = "{0} leaves {1} (back to {2} visitors in this region)";
        private string m_xmlRpcPassword = String.Empty;
        private string m_brokerURI = String.Empty;
        private int m_brokerUpdateTimeout = 300;

        internal object m_syncy = new object();

        internal bool m_enabled = false;

        #region INonSharedRegionModule Members
        public override void Initialise(IConfigSource config)
        {
            m_config = config.Configs["Concierge"];

            if (null == m_config)
            {
                //MainConsole.Instance.Info("[Concierge]: no config found, plugin disabled");
                return;
            }

            if (!m_config.GetBoolean("enabled", false))
            {
                //MainConsole.Instance.Info("[Concierge]: plugin disabled by configuration");
                return;
            }
            m_enabled = true;


            // check whether ChatModule has been disabled: if yes,
            // then we'll "stand in"
            try
            {
                if (config.Configs["Chat"] == null)
                {
                    // if Chat module has not been configured it's
                    // enabled by default, so we are not going to
                    // replace it.
                    m_replacingChatModule = false;
                }
                else 
                {
                    m_replacingChatModule  = !config.Configs["Chat"].GetBoolean("enabled", true);
                }
            }
            catch (Exception)
            {
                m_replacingChatModule = false;
            }
            MainConsole.Instance.InfoFormat("[Concierge] {0} ChatModule", m_replacingChatModule ? "replacing" : "not replacing");


            // take note of concierge channel and of identity
            m_conciergeChannel = config.Configs["Concierge"].GetInt("concierge_channel", m_conciergeChannel);
            m_whoami = m_config.GetString("whoami", "conferencier");
            m_welcomes = m_config.GetString("welcomes", m_welcomes);
            m_announceEntering = m_config.GetString("announce_entering", m_announceEntering);
            m_announceLeaving = m_config.GetString("announce_leaving", m_announceLeaving);
            m_xmlRpcPassword = m_config.GetString("password", m_xmlRpcPassword);
            m_brokerURI = m_config.GetString("broker", m_brokerURI);
            m_brokerUpdateTimeout = m_config.GetInt("broker_timeout", m_brokerUpdateTimeout);

            MainConsole.Instance.InfoFormat("[Concierge] reporting as \"{0}\" to our users", m_whoami);

            MainServer.Instance.AddXmlRPCHandler("concierge_update_welcome", XmlRpcUpdateWelcomeMethod);

            // calculate regions Regex
            if (m_regions == null)
            {
                string regions = m_config.GetString("regions", String.Empty);
                if (!String.IsNullOrEmpty(regions))
                {
                    m_regions = new Regex(@regions, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
            }
        }


        public override void AddRegion(IScene scene)
        {
            if (!m_enabled) return;

            lock (m_syncy)
            {
                m_scene = scene;

                // subscribe to NewClient events
                scene.EventManager.OnNewClient += OnNewClient;
                scene.EventManager.OnClosingClient += OnClientLoggedOut;

                // subscribe to *Chat events
                scene.EventManager.OnChatFromWorld += OnChatFromWorld;
                if (!m_replacingChatModule)
                    scene.EventManager.OnChatFromClient += OnChatFromClient;
                scene.EventManager.OnChatBroadcast += OnChatBroadcast;

                // subscribe to agent change events
                scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
                scene.EventManager.OnMakeChildAgent += OnMakeChildAgent;
            }
            MainConsole.Instance.InfoFormat("[Concierge]: initialized for {0}", scene.RegionInfo.RegionName);
        }

        public override void RemoveRegion(IScene scene)
        {
            if (!m_enabled) return;

            lock (m_syncy)
            {
                // unsubscribe from NewClient events
                scene.EventManager.OnNewClient -= OnNewClient;
                scene.EventManager.OnClosingClient -= OnClientLoggedOut;

                // unsubscribe from *Chat events
                scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
                if (!m_replacingChatModule)
                    scene.EventManager.OnChatFromClient -= OnChatFromClient;
                scene.EventManager.OnChatBroadcast -= OnChatBroadcast;

                // unsubscribe from agent change events
                scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
                scene.EventManager.OnMakeChildAgent -= OnMakeChildAgent;

                m_scene = null;
            }
            MainConsole.Instance.InfoFormat("[Concierge]: removed {0}", scene.RegionInfo.RegionName);
        }

        public override void Close()
        {
        }

        public override string Name
        {
            get { return "ConciergeModule"; }
        }
        #endregion

        #region ISimChat Members
        protected override void OnChatBroadcast(Object sender, OSChatMessage c)
        {
            if (m_replacingChatModule)
            {
                // distribute chat message to each and every avatar in
                // the region
                base.OnChatBroadcast(sender, c);
            }

            // TODO: capture logic
            return;
        }

        protected override void OnChatFromClient (IClientAPI sender, OSChatMessage c)
        {
            if (m_replacingChatModule)
            {
                // replacing ChatModule: need to redistribute
                // ChatFromClient to interested subscribers
                c = FixPositionOfChatMessage(c);

                IScene scene = c.Scene;
                scene.EventManager.TriggerOnChatFromClient(sender, c);

                // when we are replacing ChatModule, we treat
                // OnChatFromClient like OnChatBroadcast for
                // concierged regions, effectively extending the
                // range of chat to cover the whole
                // region. however, we don't do this for whisper
                // (got to have some privacy)
                if (c.Type != ChatTypeEnum.Whisper)
                {
                    base.OnChatBroadcast(sender, c);
                    return;
                }

                // redistribution will be done by base class
                base.OnChatFromClient(sender, c);
            }

            // TODO: capture chat
            return;
        }

        public override void OnChatFromWorld(Object sender, OSChatMessage c)
        {
            if (m_replacingChatModule)
            {
                // when we are replacing ChatModule, we treat
                // OnChatFromClient like OnChatBroadcast for
                // concierged regions, effectively extending the
                // range of chat to cover the whole
                // region. however, we don't do this for whisper
                // (got to have some privacy)
                if (c.Type != ChatTypeEnum.Whisper) 
                {
                    base.OnChatBroadcast(sender, c);
                    return;
                }

                base.OnChatFromWorld(sender, c);
            }
            return;
        }
        #endregion


        public override void OnNewClient(IClientAPI client)
        {
            client.OnLogout += OnClientLoggedOut;

            if (m_replacingChatModule) 
                client.OnChatFromClient += OnChatFromClient;
        }

        public void OnClientLoggedOut(IClientAPI client)
        {
            client.OnLogout -= OnClientLoggedOut;
            client.OnConnectionClosed -= OnClientLoggedOut;

            if (m_replacingChatModule)
                client.OnChatFromClient -= OnChatFromClient;
            
            IEntityCountModule entityCountModule = m_scene.RequestModuleInterface<IEntityCountModule>();
            if (entityCountModule != null)
            {
                MainConsole.Instance.DebugFormat("[Concierge]: {0} logs off from {1}", client.Name, m_scene.RegionInfo.RegionName);
                AnnounceToAgentsRegion(m_scene, String.Format(m_announceLeaving, client.Name, m_scene.RegionInfo.RegionName, entityCountModule.RootAgents));
                UpdateBroker(m_scene);
            }
        }

        public void OnMakeRootAgent(IScenePresence agent)
        {
            MainConsole.Instance.DebugFormat("[Concierge]: {0} enters {1}", agent.Name, m_scene.RegionInfo.RegionName);
            WelcomeAvatar(agent, m_scene);
            IEntityCountModule entityCountModule = m_scene.RequestModuleInterface<IEntityCountModule>();
            if (entityCountModule != null)
            {
                AnnounceToAgentsRegion(m_scene, String.Format(m_announceEntering, agent.Name,
                                                            m_scene.RegionInfo.RegionName, entityCountModule.RootAgents));
                UpdateBroker(m_scene);
            }
        }


        public void OnMakeChildAgent(IScenePresence agent, GridRegion destination)
        {
            MainConsole.Instance.DebugFormat("[Concierge]: {0} leaves {1}", agent.Name, m_scene.RegionInfo.RegionName);
            IEntityCountModule entityCountModule = m_scene.RequestModuleInterface<IEntityCountModule>();
            if (entityCountModule != null)
            {
                AnnounceToAgentsRegion(m_scene, String.Format(m_announceLeaving, agent.Name,
                                                           m_scene.RegionInfo.RegionName, entityCountModule.RootAgents));
                UpdateBroker(m_scene);
            }
        }

        internal class BrokerState
        {
            public string Uri;
            public string Payload;
            public HttpWebRequest Poster;
            public Timer Timer;

            public BrokerState(string uri, string payload, HttpWebRequest poster)
            {
                Uri = uri;
                Payload = payload;
                Poster = poster;
            }
        }

        protected void UpdateBroker(IScene scene)
        {
            if (String.IsNullOrEmpty(m_brokerURI))
                return;

            string uri = String.Format(m_brokerURI, scene.RegionInfo.RegionName, scene.RegionInfo.RegionID);

            // create XML sniplet
            StringBuilder list = new StringBuilder();
            IEntityCountModule entityCountModule = scene.RequestModuleInterface<IEntityCountModule>();
            if (entityCountModule != null)
            {
                list.Append(String.Format("<avatars count=\"{0}\" region_name=\"{1}\" region_uuid=\"{2}\" timestamp=\"{3}\">\n",
                                         entityCountModule.RootAgents, scene.RegionInfo.RegionName,
                                         scene.RegionInfo.RegionID,
                                         DateTime.UtcNow.ToString("s")));
            }
            scene.ForEachScenePresence(delegate(IScenePresence sp)
            {
                if (!sp.IsChildAgent)
                {
                    list.Append(String.Format("    <avatar name=\"{0}\" uuid=\"{1}\" />\n", sp.Name, sp.UUID));
                    list.Append("</avatars>");
                }
            });
            string payload = list.ToString();

            // post via REST to broker
            HttpWebRequest updatePost = WebRequest.Create(uri) as HttpWebRequest;
            updatePost.Method = "POST";
            updatePost.ContentType = "text/xml";
            updatePost.ContentLength = payload.Length;
            updatePost.UserAgent = "OpenSim.Concierge";


            BrokerState bs = new BrokerState(uri, payload, updatePost);
            bs.Timer = new Timer(delegate(object state)
                                 {
                                     BrokerState b = state as BrokerState;
                                     b.Poster.Abort();
                                     b.Timer.Dispose();
                                     MainConsole.Instance.Debug("[Concierge]: async broker POST abort due to timeout");
                                 }, bs, m_brokerUpdateTimeout * 1000, Timeout.Infinite);

            try
            {
                updatePost.BeginGetRequestStream(UpdateBrokerSend, bs);
                MainConsole.Instance.DebugFormat("[Concierge] async broker POST to {0} started", uri);
            }
            catch (WebException we)
            {
                MainConsole.Instance.ErrorFormat("[Concierge] async broker POST to {0} failed: {1}", uri, we.Status);
            }
        }

        private void UpdateBrokerSend(IAsyncResult result)
        {
            BrokerState bs = null;
            try
            {
                bs = result.AsyncState as BrokerState;
                string payload = bs.Payload;
                HttpWebRequest updatePost = bs.Poster;

                using (StreamWriter payloadStream = new StreamWriter(updatePost.EndGetRequestStream(result)))
                {
                    payloadStream.Write(payload);
                    payloadStream.Close();
                }
                updatePost.BeginGetResponse(UpdateBrokerDone, bs);
            }
            catch (WebException we)
            {
                MainConsole.Instance.DebugFormat("[Concierge]: async broker POST to {0} failed: {1}", bs.Uri, we.Status);
            }
            catch (Exception)
            {
                MainConsole.Instance.DebugFormat("[Concierge]: async broker POST to {0} failed", bs.Uri);
            }
        }

        private void UpdateBrokerDone(IAsyncResult result)
        {
            BrokerState bs = null;
            try 
            {
                bs = result.AsyncState as BrokerState;
                HttpWebRequest updatePost = bs.Poster;
                using (HttpWebResponse response = updatePost.EndGetResponse(result) as HttpWebResponse)
                {
                    MainConsole.Instance.DebugFormat("[Concierge] broker update: status {0}", response.StatusCode);
                }
                bs.Timer.Dispose();
            }
            catch (WebException we)
            {
                MainConsole.Instance.ErrorFormat("[Concierge] broker update to {0} failed with status {1}", bs.Uri, we.Status);
                if (null != we.Response) 
                {
                    using (HttpWebResponse resp = we.Response as HttpWebResponse)
                    {
                        MainConsole.Instance.ErrorFormat("[Concierge] response from {0} status code: {1}", bs.Uri, resp.StatusCode);
                        MainConsole.Instance.ErrorFormat("[Concierge] response from {0} status desc: {1}", bs.Uri, resp.StatusDescription);
                        MainConsole.Instance.ErrorFormat("[Concierge] response from {0} server:      {1}", bs.Uri, resp.Server);
                        
                        if (resp.ContentLength > 0) 
                        {
                            StreamReader content = new StreamReader(resp.GetResponseStream());
                            MainConsole.Instance.ErrorFormat("[Concierge] response from {0} content:     {1}", bs.Uri, content.ReadToEnd());
                            content.Close();
                        }
                    }
                }
            }
        }

        protected void WelcomeAvatar (IScenePresence agent, IScene scene)
        {
            // welcome mechanics: check whether we have a welcomes
            // directory set and wether there is a region specific
            // welcome file there: if yes, send it to the agent
            if (!String.IsNullOrEmpty(m_welcomes))
            {
                string[] welcomes = new string[] { 
                    Path.Combine(m_welcomes, agent.Scene.RegionInfo.RegionName),
                    Path.Combine(m_welcomes, "DEFAULT")};
                foreach (string welcome in welcomes)
                {
                    if (File.Exists(welcome)) 
                    {
                        try
                        {
                            string[] welcomeLines = File.ReadAllLines(welcome);
                            foreach (string l in welcomeLines)
                            {
                                AnnounceToAgent(agent, String.Format(l, agent.Name, scene.RegionInfo.RegionName, m_whoami));
                            }
                        }
                        catch (IOException ioe)
                        {
                            MainConsole.Instance.ErrorFormat("[Concierge]: run into trouble reading welcome file {0} for region {1} for avatar {2}: {3}",
                                             welcome, scene.RegionInfo.RegionName, agent.Name, ioe);
                        }
                        catch (FormatException fe)
                        {
                            MainConsole.Instance.ErrorFormat("[Concierge]: welcome file {0} is malformed: {1}", welcome, fe);
                        }
                    } 
                    return;
                }
                MainConsole.Instance.DebugFormat("[Concierge]: no welcome message for region {0}", scene.RegionInfo.RegionName);
            }
        }

        static private Vector3 PosOfGod = new Vector3(128, 128, 9999);

        // protected void AnnounceToAgentsRegion(Scene scene, string msg)
        // {
        //     ScenePresence agent = null;
        //     if ((client.Scene is Scene) && (client.Scene as Scene).TryGetScenePresence(client.AgentId, out agent)) 
        //         AnnounceToAgentsRegion(agent, msg);
        //     else
        //         MainConsole.Instance.DebugFormat("[Concierge]: could not find an agent for client {0}", client.Name);
        // }

        protected void AnnounceToAgentsRegion(IScene scene, string msg)
        {
            OSChatMessage c = new OSChatMessage();
            c.Message = msg;
            c.Type = ChatTypeEnum.Say;
            c.Channel = 0;
            c.Position = PosOfGod;
            c.From = m_whoami;
            c.Sender = null;
            c.SenderUUID = UUID.Zero;
            c.Scene = scene;

            scene.EventManager.TriggerOnChatBroadcast(this, c);
        }

        protected void AnnounceToAgent (IScenePresence agent, string msg)
        {
            OSChatMessage c = new OSChatMessage();
            c.Message = msg;
            c.Type = ChatTypeEnum.Say;
            c.Channel = 0;
            c.Position = PosOfGod;
            c.From = m_whoami;
            c.Sender = null;
            c.SenderUUID = UUID.Zero;
            c.Scene = agent.Scene;

            agent.ControllingClient.SendChatMessage(msg, (byte) ChatTypeEnum.Say, PosOfGod, m_whoami, UUID.Zero, 
                                                    (byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
        }

        private static void checkStringParameters(XmlRpcRequest request, string[] param)
        {
            Hashtable requestData = (Hashtable) request.Params[0];
            foreach (string p in param)
            {
                if (!requestData.Contains(p))
                    throw new Exception(String.Format("missing string parameter {0}", p));
                if (String.IsNullOrEmpty((string)requestData[p]))
                    throw new Exception(String.Format("parameter {0} is empty", p));
            }
        }

        public XmlRpcResponse XmlRpcUpdateWelcomeMethod(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            MainConsole.Instance.Info("[Concierge]: processing UpdateWelcome request");
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                checkStringParameters(request, new string[] { "password", "region", "welcome" });

                // check password
                if (!String.IsNullOrEmpty(m_xmlRpcPassword) &&
                    (string)requestData["password"] != m_xmlRpcPassword) throw new Exception("wrong password");

                if (String.IsNullOrEmpty(m_welcomes))
                    throw new Exception("welcome templates are not enabled, ask your OpenSim operator to set the \"welcomes\" option in the [Concierge] section of Aurora.ini");

                string msg = (string)requestData["welcome"];
                if (String.IsNullOrEmpty(msg))
                    throw new Exception("empty parameter \"welcome\"");

                string regionName = (string)requestData["region"];

                string welcome = Path.Combine(m_welcomes, regionName);
                if (File.Exists(welcome))
                {
                    MainConsole.Instance.InfoFormat("[Concierge]: UpdateWelcome: updating existing template \"{0}\"", welcome);
                    string welcomeBackup = String.Format("{0}~", welcome);
                    if (File.Exists(welcomeBackup))
                        File.Delete(welcomeBackup);
                    File.Move(welcome, welcomeBackup);
                }
                File.WriteAllText(welcome, msg);

                responseData["success"] = "true";
                response.Value = responseData;
            }
            catch (Exception e)
            {
                MainConsole.Instance.InfoFormat("[Concierge]: UpdateWelcome failed: {0}", e.Message);

                responseData["success"] = "false";
                responseData["error"] = e.Message;

                response.Value = responseData;
            }
            MainConsole.Instance.Debug("[Concierge]: done processing UpdateWelcome request");
            return response;
        }
    }
}
