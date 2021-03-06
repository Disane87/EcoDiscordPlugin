﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Net.WebSocket;
using Eco.Core;
using Eco.Core.Plugins;
using Eco.Core.Plugins.Interfaces;
using Eco.Core.Utils;
using Eco.Gameplay.Players;
using Eco.Gameplay.Systems.Chat;
using Eco.Shared.Services;
using Eco.Shared.Utils;

namespace Eco.Plugins.DiscordLink
{
    public class DiscordLink : IModKitPlugin, IInitializablePlugin, IConfigurablePlugin
    {
        protected string NametagColor = "7289DAFF";
        private PluginConfig<DiscordConfig> configOptions;
        private DiscordClient _discordClient;
        private CommandsNextModule _commands;
        private string _currentToken;
        private string _status = "No Connection Attempt Made";
        private const string CommandPrefix = "?";

        private static readonly Regex TagStripRegex = new Regex("<[^>]*>");

        protected ChatNotifier chatNotifier;

        public override string ToString()
        {
            return "DiscordLink";
        }

        public IPluginConfig PluginConfig
        {
            get { return configOptions; }
        }

        public DiscordConfig DiscordPluginConfig
        {
            get { return PluginConfig.GetConfig() as DiscordConfig; }
        }

        public string GetStatus()
        {
            return _status;
        }

        public void Initialize(TimedTask timer)
        {
            if (_discordClient == null) return;
            ConnectAsync();
            StartChatNotifier();
        }

        private void StartChatNotifier()
        {
            chatNotifier.Initialize();
            new Thread(() => { chatNotifier.Run(); })
            {
                Name = "ChatNotifierThread"
            }.Start();
        }

        public DiscordLink()
        {
            SetupConfig();
            chatNotifier = new ChatNotifier();
            SetUpClient();
        }

        private void SetupConfig()
        {
            configOptions = new PluginConfig<DiscordConfig>("DiscordPluginSpoffy");
            DiscordPluginConfig.ChannelLinks.CollectionChanged += (obj, args) => { SaveConfig(); };
        }

        #region DiscordClient Management

        private async Task<object> DisposeOfClient()
        {
            if (_discordClient != null)
            {
                await DisconnectAsync();
                _discordClient.Dispose();
            }

            return null;
        }

        private bool SetUpClient()
        {
            DisposeOfClient();
            _status = "Setting up client";
            // Loading the configuration
            _currentToken = String.IsNullOrWhiteSpace(DiscordPluginConfig.BotToken)
                ? "ThisTokenWillNeverWork" //Whitespace isn't allowed, and it should trigger an obvious authentication error rather than crashing.
                : DiscordPluginConfig.BotToken;

            try
            {
                // Create the new client
                _discordClient = new DiscordClient(new DiscordConfiguration
                {
                    AutoReconnect = true,
                    Token = _currentToken,
                    TokenType = TokenType.Bot
                });
                _discordClient.SetWebSocketClient<WebSocket4NetClient>();

                _discordClient.Ready += async args => { Logger.Info("Connected and Ready"); };
                _discordClient.ClientErrored += async args => { Logger.Error(args.EventName + " " + args.Exception.ToString()); };
                _discordClient.SocketErrored += async args => { Logger.Error(args.Exception.ToString()); };
                _discordClient.SocketClosed += async args => { Logger.DebugVerbose("Socket Closed: " + args.CloseMessage + " " + args.CloseCode); };
                _discordClient.Resumed += async args => { Logger.Info("Resumed connection"); };

                // Set up the client to use CommandsNext
                _commands = _discordClient.UseCommandsNext(new CommandsNextConfiguration
                {
                    StringPrefix = CommandPrefix
                });

                _commands.RegisterCommands<DiscordDiscordCommands>();

                return true;
            }
            catch (Exception e)
            {
                Logger.Error("ERROR: Unable to create the discord client. Error message was: " + e.Message + "\n");
                Logger.Error("Backtrace: " + e.StackTrace);
            }

            return false;
        }

        public async Task<bool> RestartClient()
        {
            var result = SetUpClient();
            await ConnectAsync();
            return result;
        }
        public async Task<object> ConnectAsync()
        {
            try
            {
                _status = "Attempting connection...";
                await _discordClient.ConnectAsync();
                BeginRelaying();
                Logger.Info("Connected to Discord.\n");
                _status = "Connection successful";


            }
            catch (Exception e)
            {
                Logger.Error("Error connecting to discord: " + e.Message + "\n");
                _status = "Connection failed";
            }

            return null;
        }

        public async Task<object> DisconnectAsync()
        {
            try
            {
                StopRelaying();
                await _discordClient.DisconnectAsync();
            }
            catch (Exception e)
            {
                Logger.Error("Disconnecting from discord: " + e.Message + "\n");
                _status = "Connection failed";
            }

            return null;
        }

        public DiscordClient DiscordClient => _discordClient;

        #endregion

        #region Discord Guild Access

        public string[] GuildNames => _discordClient.GuildNames();
        public DiscordGuild DefaultGuild => _discordClient.DefaultGuild();
        
        public DiscordGuild GuildByName(string name)
        {
            return _discordClient.GuildByName(name);
        }

        public DiscordGuild GuildByNameOrId(string nameOrId)
        {
            var maybeGuildId = DSharpExtensions.TryParseSnowflakeId(nameOrId);
            return maybeGuildId != null ? _discordClient.Guilds[maybeGuildId.Value] : GuildByName(nameOrId);
        }

        #endregion

        #region Message Sending

        public async Task<string> SendMessage(string message, string channelNameOrId, string guildNameOrId)
        {
            if (_discordClient == null) return "No discord client";

            var guild = GuildByNameOrId(guildNameOrId);
            if (guild == null) return "No guild of that name found";

            var channel = guild.ChannelByNameOrId(channelNameOrId);
            return await SendMessage(message, channel);
        }

        private string FormatMessageFromUsername(string message, string username)
        {
            return $"**{username}**: {StripTags(message)}";
        }

        public async Task<string> SendMessage(string message, DiscordChannel channel)
        {
            if (_discordClient == null) return "No discord client";
            if (channel == null) return "No channel of that name or ID found in that guild";

            await _discordClient.SendMessageAsync(channel, message);
            return "Message sent successfully!";
        }

        public async Task<string> SendMessageAsUser(string message, User user, string channelName, string guildName)
        {
            return await SendMessage(FormatMessageFromUsername(message, user.Name), channelName, guildName);
        }

        public async Task<String> SendMessageAsUser(string message, User user, DiscordChannel channel)
        {
            return await SendMessage(FormatMessageFromUsername(message, user.Name), channel);
        }

        #endregion

        #region MessageRelaying

        private string EcoUserSteamId = "DiscordLinkSteam";
        private string EcoUserSlgId = "DiscordLinkSlg";
        private string EcoUserName = "Discord";
        private User _ecoUser;
        private bool _relayInitialised = false;

        protected User EcoUser =>
            _ecoUser ?? (_ecoUser = UserManager.GetOrCreateUser(EcoUserSteamId, EcoUserSlgId, EcoUserName));

        private void BeginRelaying()
        {
            if (!_relayInitialised)
            {
                chatNotifier.OnMessageReceived.Add(OnMessageReceivedFromEco);
                _discordClient.MessageCreated += OnDiscordMessageCreateEvent;
            }

            _relayInitialised = true;
        }

        private void StopRelaying()
        {
            if (_relayInitialised)
            {
                chatNotifier.OnMessageReceived.Remove(OnMessageReceivedFromEco);
                _discordClient.MessageCreated -= OnDiscordMessageCreateEvent;
            }

            _relayInitialised = false;
        }

        private ChannelLink GetLinkForEcoChannel(string discordChannelNameOrId)
        {
            return DiscordPluginConfig.ChannelLinks.FirstOrDefault(link => link.DiscordChannel == discordChannelNameOrId);
        }

        private ChannelLink GetLinkForDiscordChannel(string ecoChannelName)
        {
            var lowercaseEcoChannelName = ecoChannelName.ToLower();
            return DiscordPluginConfig.ChannelLinks.FirstOrDefault(link => link.EcoChannel.ToLower() == lowercaseEcoChannelName);
        }

        public static string StripTags(string toStrip)
        {
            return TagStripRegex.Replace(toStrip, String.Empty);
        }

        public void LogEcoMessage(ChatMessage message)
        {
            Logger.DebugVerbose("Eco Message Processed:");
            Logger.DebugVerbose("Message: " + message.Text);
            Logger.DebugVerbose("Tag: " + message.Tag);
            Logger.DebugVerbose("Category: " + message.Category);
            Logger.DebugVerbose("Temporary: " + message.Temporary);
            Logger.DebugVerbose("Sender: " + message.Sender);
        }

        public void OnMessageReceivedFromEco(ChatMessage message)
        {
            LogEcoMessage(message);
            if (message.Sender == EcoUser.Name) { return; }
            if (String.IsNullOrWhiteSpace(message.Sender)) { return; };

            //Remove the # character from the start.
            var channelLink = GetLinkForDiscordChannel(message.Tag.Substring(1));
            var channel = channelLink?.DiscordChannel;
            var guild = channelLink?.DiscordGuild;

            if (!String.IsNullOrWhiteSpace(channel) && !String.IsNullOrWhiteSpace(guild))
            {
                Logger.DebugVerbose("Sending Eco message to Discord");
                SendMessage(FormatMessageFromUsername(message.Text, message.Sender), channel, guild);
            }
        }

        public async Task OnDiscordMessageCreateEvent(MessageCreateEventArgs messageArgs)
        {
            OnMessageReceivedFromDiscord(messageArgs.Message);
        }

        public void OnMessageReceivedFromDiscord(DiscordMessage message)
        {
            Logger.DebugVerbose("Message received from Discord on channel: " + message.Channel.Name);
            if (message.Author == _discordClient.CurrentUser) { return; }
            if (message.Content.StartsWith(CommandPrefix)) { return; }
            
            var channelLink = GetLinkForEcoChannel(message.Channel.Name) ?? GetLinkForEcoChannel(message.Channel.Id.ToString());
            var channel = channelLink?.EcoChannel;
            if (!String.IsNullOrWhiteSpace(channel))
            {
                ForwardMessageToEcoChannel(message, channel);
            }
        }

        private async void ForwardMessageToEcoChannel(DiscordMessage message, string channelName)
        {
            Logger.DebugVerbose("Sending message to Eco channel: " + channelName);
            var author = await message.Channel.Guild.MaybeGetMemberAsync(message.Author.Id);
            var nametag = author != null
                ? Text.Bold(Text.Color(NametagColor, author.DisplayName))
                : message.Author.Username;
            var text = $"#{channelName} {nametag}: {GetReadableContent(message)}";
            ChatManager.SendChat(text, EcoUser);
        }

        private String GetReadableContent(DiscordMessage message)
        {
            var content = message.Content;
            foreach (var user in message.MentionedUsers)
            {
                if (user == null) { continue; }
                DiscordMember member = message.Channel.Guild.Members.FirstOrDefault(m => m?.Id == user.Id);
                if (member == null) { continue; }
                String name = "@" + member.DisplayName;
                content = content.Replace($"<@{user.Id}>", name)
                        .Replace($"<@!{user.Id}>", name);
            }
            foreach (var role in message.MentionedRoles)
            {
                if (role == null) continue;
                content = content.Replace($"<@&{role.Id}>", $"@{role.Name}");
            }
            foreach (var channel in message.MentionedChannels)
            {
                if (channel == null) continue;
                content = content.Replace($"<#{channel.Id}>", $"#{channel.Name}");
            }
            return content;
        }

        #endregion

        #region Configuration

        public static DiscordLink Obj
        {
            get { return PluginManager.GetPlugin<DiscordLink>(); }
        }

        public object GetEditObject()
        {
            return configOptions.Config;
        }

        public void OnEditObjectChanged(object o, string param)
        {
            SaveConfig();
        }

        protected void SaveConfig()
        {
            Logger.DebugVerbose("Saving Config");
            configOptions.Save();
            if (DiscordPluginConfig.BotToken != _currentToken)
            {
                //Reinitialise client.
                Logger.Info("Discord Token changed, reinitialising client.\n");
                RestartClient();
            }
        }

        #endregion

        #region Player Configs

        public DiscordPlayerConfig GetOrCreatePlayerConfig(string identifier)
        {
            var config = DiscordPluginConfig.PlayerConfigs.FirstOrDefault(user => user.Username == identifier);
            if (config == null)
            {
                config = new DiscordPlayerConfig
                {
                    Username = identifier
                };
                AddOrReplacePlayerConfig(config);
            }

            return config;
        }

        public bool AddOrReplacePlayerConfig(DiscordPlayerConfig config)
        {
            var removed = DiscordPluginConfig.PlayerConfigs.Remove(config);
            DiscordPluginConfig.PlayerConfigs.Add(config);
            SavePlayerConfig();
            return removed;
        }

        public void SavePlayerConfig()
        {
            configOptions.Save();
        }

        public DiscordChannel GetDefaultChannelForPlayer(string identifier)
        {
            var playerConfig = GetOrCreatePlayerConfig(identifier);
            if (playerConfig.DefaultChannel == null
                || String.IsNullOrEmpty(playerConfig.DefaultChannel.Guild)
                || String.IsNullOrEmpty(playerConfig.DefaultChannel.Channel))
            {
                return null;
            }

            return GuildByName(playerConfig.DefaultChannel.Guild).ChannelByName(playerConfig.DefaultChannel.Channel);
        }


        public void SetDefaultChannelForPlayer(string identifier, string guildName, string channelName)
        {
            var playerConfig = GetOrCreatePlayerConfig(identifier);
            playerConfig.DefaultChannel.Guild = guildName;
            playerConfig.DefaultChannel.Channel = channelName;
            SavePlayerConfig();
        }

        #endregion
    }

    public class DiscordConfig
    {
        [Description("The token provided by the Discord API to allow access to the bot"), Category("Bot Configuration")]
        public string BotToken { get; set; }

        [Description("The name of the Eco server, overriding the name configured within Eco."), Category("Server Details")]
        public string ServerName { get; set; }

        [Description("The description of the Eco server, overriding the description configured within Eco."), Category("Server Details")]
        public string ServerDescription { get; set; }

        [Description("The logo of the server as a URL."), Category("Server Details")]
        public string ServerLogo { get; set; }

        [Description("IP of the server. Overrides the automatically detected IP."), Category("Server Details")]
        public string ServerIP { get; set; }

        private List<DiscordPlayerConfig> _playerConfigs = new List<DiscordPlayerConfig>();

        [Description("A mapping from user to user config parameters.")]
        public List<DiscordPlayerConfig> PlayerConfigs
        {
            get
            {
                return _playerConfigs;
            }
            set
            {
                _playerConfigs = value;
            }
        }

        [Description("Channels to connect together."), Category("Channel Configuration")]
        public ObservableCollection<ChannelLink> ChannelLinks { get; set; } = new ObservableCollection<ChannelLink>();

        [Description("Enables debugging output to the console."), Category("Debugging")]
        public bool Debug { get; set; } = false;
    }

    public class DiscordPlayerConfig
    {
        [Description("ID of the user")]
        public string Username { get; set; }

        private DiscordChannelIdentifier _defaultChannel = new DiscordChannelIdentifier();
        public DiscordChannelIdentifier DefaultChannel
        {
            get { return _defaultChannel; }
            set { _defaultChannel = value; }
        }

        public class DiscordChannelIdentifier
        {
            public string Guild { get; set; }
            public string Channel { get; set; }
        }
    }

    public class ChannelLink
    {
        [Description("Discord Guild channel is in by name or ID. Case sensitive.")]
        public string DiscordGuild { get; set; }

        [Description("Discord Channel to use by name or ID. Case sensitive.")]
        public string DiscordChannel { get; set; }

        [Description("Eco Channel to use. Case sensitive.")]
        public string EcoChannel { get; set; }
    }
}