namespace BnkExtractor.BnkExtr
{
    public enum EventActionType : sbyte
    {
        Stop = 1,
        Pause = 2,
        Resume = 3,
        Play = 4,
        Trigger = 5,
        Mute = 6,
        UnMute = 7,
        SetVoicePitch = 8,
        ResetVoicePitch = 9,
        SetVpoceVolume = 10,
        ResetVoiceVolume = 11,
        SetBusVolume = 12,
        ResetBusVolume = 13,
        SetVoiceLowPassFilter = 14,
        ResetVoiceLowPassFilter = 15,
        EnableState = 16,
        DisableState = 17,
        SetState = 18,
        SetGameParameter = 19,
        ResetGameParameter = 20,
        SetSwitch = 21,
        ToggleBypass = 22,
        ResetBypassEffect = 23,
        Break = 24,
        Seek = 25,
    }
}
