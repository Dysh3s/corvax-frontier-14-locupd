using Content.Server.Administration.Logs;
using Content.Server.Chat.Systems;
using Content.Server.Power.Components;
using Content.Server.Radio.Components;
using Content.Server.VoiceMask;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Robust.Server.GameObjects;
using Content.Shared.Speech;
using Content.Shared.Ghost; // Corvax-Frontier
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Replays;
using Robust.Shared.Utility;
using Content.Shared.IdentityManagement;
using Content.Server.Corvax.Language.EntitySystems;
using Content.Server.Corvax.Language;

namespace Content.Server.Radio.EntitySystems;

/// <summary>
///     This system handles intrinsic radios and the general process of converting radio messages into chat messages.
/// </summary>
public sealed class RadioSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netMan = default!;
    [Dependency] private readonly IReplayRecordingManager _replay = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly LanguageSystem _language = default!;

    // set used to prevent radio feedback loops.
    private readonly HashSet<string> _messages = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<IntrinsicRadioReceiverComponent, RadioReceiveEvent>(OnIntrinsicReceive);
        SubscribeLocalEvent<IntrinsicRadioTransmitterComponent, EntitySpokeEvent>(OnIntrinsicSpeak);
    }

    private void OnIntrinsicSpeak(EntityUid uid, IntrinsicRadioTransmitterComponent component, EntitySpokeEvent args)
    {
        if (args.Channel != null && component.Channels.Contains(args.Channel.ID))
        {
            SendRadioMessage(uid, args.Message, args.Channel, uid);
            args.Channel = null; // prevent duplicate messages from other listeners.
        }
    }

    private void OnIntrinsicReceive(EntityUid uid, IntrinsicRadioReceiverComponent component, ref RadioReceiveEvent args)
    {
        if (TryComp(uid, out ActorComponent? actor))
            _netMan.ServerSendMessage(_language.IsUnderstandLanguage(uid, args.Message) ? args.ChatMsg : args.LanguageChatMsg!, actor.PlayerSession.Channel);
    }

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    public void SendRadioMessage(EntityUid messageSource, LanguageMessage message, ProtoId<RadioChannelPrototype> channel, EntityUid radioSource, bool escapeMarkup = true)
    {
        SendRadioMessage(messageSource, message, _prototype.Index(channel), radioSource, escapeMarkup: escapeMarkup);
    }

    /// <summary>
    /// Gets the message frequency, if there is no such frequency, returns the standard channel frequency.
    /// </summary>
    public int GetFrequency(EntityUid source, RadioChannelPrototype channel)
    {
        if (TryComp<RadioMicrophoneComponent>(source, out var radioMicrophone))
            return radioMicrophone.Frequency;

        return channel.Frequency;
    } // Corvax-Frontier

    /// <summary>
    /// Send radio message to all active radio listeners
    /// </summary>
    /// <param name="messageSource">Entity that spoke the message</param>
    /// <param name="radioSource">Entity that picked up the message and will send it, e.g. headset</param>
    public void SendRadioMessage(EntityUid messageSource, LanguageMessage message, RadioChannelPrototype channel, EntityUid radioSource, int? frequency = null, bool escapeMarkup = true)
    {
        // TODO if radios ever garble / modify messages, feedback-prevention needs to be handled better than this.
        if (!_messages.Add(message.OriginalMessage))
            return;

        var name = MetaData(messageSource).EntityName; // Frontier - code block to allow multi masks.
        var mode = "Unknown";

        if (TryComp(messageSource, out VoiceMaskComponent? mask) && mask.Enabled)
        {
            switch (mask.Mode)
            {
                case Mode.Real:
                    mode = Identity.Name(messageSource, EntityManager);
                    break;
                case Mode.Fake:
                    mode = mask.VoiceName;
                    break;
                case Mode.Unknown:
                    break;
                default:
                    throw new ArgumentOutOfRangeException($"No implemented mask radio behavior for {mask.Mode}!");
            }
            name = mode;
        } // Frontier - code block to allow multi masks.

        name = FormattedMessage.EscapeText(name);

        SpeechVerbPrototype speech;
        if (mask != null
            && mask.Enabled
            && mask.SpeechVerb != null
            && _prototype.TryIndex<SpeechVerbPrototype>(mask.SpeechVerb, out var proto))
            speech = proto;
        else
            speech = _chat.GetSpeechVerb(messageSource, message.OriginalMessage);

        var content = escapeMarkup
            ? FormattedMessage.EscapeText(message.OriginalMessage)
            : message.OriginalMessage;

        var languageContent = message.Message is not null ? escapeMarkup
            ? FormattedMessage.EscapeText(message.Message)
            : message.Message : null;

        var wrappedMessage = Loc.GetString(speech.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            ("color", channel.Color),
            ("fontType", speech.FontId),
            ("fontSize", speech.FontSize),
            ("verb", Loc.GetString(_random.Pick(speech.SpeechVerbStrings))),
            ("channel", $"\\[{channel.LocalizedName}\\]"),
            ("name", name),
            ("message", content));

        var wrappedLanguageMessage = languageContent is not null ? Loc.GetString(speech!.Bold ? "chat-radio-message-wrap-bold" : "chat-radio-message-wrap",
            ("color", channel.Color),
            ("fontType", speech.FontId),
            ("fontSize", speech.FontSize),
            ("verb", Loc.GetString(_random.Pick(speech.SpeechVerbStrings))),
            ("channel", $"\\[{channel.LocalizedName}\\]"),
            ("name", name),
            ("message", languageContent)) : null;

        // most radios are relayed to chat, so lets parse the chat message beforehand
        var chat = new ChatMessage(
            ChatChannel.Radio,
            message.OriginalMessage,
            wrappedMessage,
            NetEntity.Invalid,
            null);
        var chatMsg = new MsgChatMessage { Message = chat };

        var languageChat = wrappedLanguageMessage is not null ? new ChatMessage(
            ChatChannel.Radio,
            message.Message!,
            wrappedLanguageMessage,
            NetEntity.Invalid,
            null) : null;
        var languageChatMsg = languageChat is not null ? new MsgChatMessage { Message = languageChat } : null;

        var ev = new RadioReceiveEvent(message, messageSource, channel, chatMsg, languageChatMsg);

        var sendAttemptEv = new RadioSendAttemptEvent(channel, radioSource);
        RaiseLocalEvent(ref sendAttemptEv);
        RaiseLocalEvent(radioSource, ref sendAttemptEv);
        var canSend = !sendAttemptEv.Cancelled;

        var sourceMapId = Transform(radioSource).MapID;
        var hasActiveServer = HasActiveServer(sourceMapId, channel.ID);
        var hasMicro = HasComp<RadioMicrophoneComponent>(radioSource);

        var speakerQuery = GetEntityQuery<RadioSpeakerComponent>();
        var radioQuery = EntityQueryEnumerator<ActiveRadioComponent, TransformComponent>();

        if (frequency == null)
            frequency = GetFrequency(messageSource, channel);

        while (canSend && radioQuery.MoveNext(out var receiver, out var radio, out var transform))
        {
            if (!radio.ReceiveAllChannels)
            {
                if (!radio.Channels.Contains(channel.ID) || (TryComp<IntercomComponent>(receiver, out var intercom) &&
                                                             !intercom.SupportedChannels.Contains(channel.ID)))
                    continue;
            }
            // Corvax-Frontier
            if (!HasComp<GhostComponent>(receiver) && GetFrequency(receiver, channel) != frequency)
                continue;

            if (!channel.LongRange && transform.MapID != sourceMapId && !radio.GlobalReceive)
                continue;

            // don't need telecom server for long range channels or handheld radios and intercoms
            var needServer = !channel.LongRange && (!hasMicro || !speakerQuery.HasComponent(receiver));
            if (needServer && !hasActiveServer)
                continue;

            // check if message can be sent to specific receiver
            var attemptEv = new RadioReceiveAttemptEvent(channel, radioSource, receiver);
            RaiseLocalEvent(ref attemptEv);
            RaiseLocalEvent(receiver, ref attemptEv);
            if (attemptEv.Cancelled)
                continue;

            // send the message
            RaiseLocalEvent(receiver, ref ev);
        }

        if (name != Name(messageSource))
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} as {name} on {channel.LocalizedName}: {message}");
        else
            _adminLogger.Add(LogType.Chat, LogImpact.Low, $"Radio message from {ToPrettyString(messageSource):user} on {channel.LocalizedName}: {message}");

        _replay.RecordServerMessage(chat);
        _messages.Remove(message.OriginalMessage);
    }

    /// <inheritdoc cref="TelecomServerComponent"/>
    private bool HasActiveServer(MapId mapId, string channelId)
    {
        var servers = EntityQuery<TelecomServerComponent, EncryptionKeyHolderComponent, ApcPowerReceiverComponent, TransformComponent>();
        foreach (var (_, keys, power, transform) in servers)
        {
            if (transform.MapID == mapId &&
                power.Powered &&
                keys.Channels.Contains(channelId))
            {
                return true;
            }
        }
        return false;
    }
}
