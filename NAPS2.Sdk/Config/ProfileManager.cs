using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using NAPS2.Scan;
using NAPS2.Serialization;
using NAPS2.Util;

namespace NAPS2.Config;

// TODO: Fix cross-instance contention
public class ProfileManager : IProfileManager
{
    private readonly ISerializer<ProfileConfig> _serializer = new ProfileSerializer();
    private readonly FileConfigScope<ProfileConfig> _userScope;
    private readonly FileConfigScope<ProfileConfig> _appScope;
    private readonly bool _userPathExisted;
    private readonly bool _lockSystemProfiles;
    private readonly bool _lockUnspecifiedDevices;
    private readonly bool _noUserProfiles;

    private List<ScanProfile> _profiles;

    public ProfileManager(string userPath, string systemPath, bool lockSystemProfiles, bool lockUnspecifiedDevices, bool noUserProfiles)
    {
        _userPathExisted = File.Exists(userPath);
        _userScope = ConfigScope.File(userPath, () => new ProfileConfig(), _serializer, ConfigScopeMode.ReadWrite);
        _appScope = ConfigScope.File(systemPath, () => new ProfileConfig(), _serializer, ConfigScopeMode.ReadOnly);
        _lockSystemProfiles = lockSystemProfiles;
        _lockUnspecifiedDevices = lockUnspecifiedDevices;
        _noUserProfiles = noUserProfiles;
    }

    public event EventHandler? ProfilesUpdated;

    public ImmutableList<ScanProfile> Profiles
    {
        get
        {
            lock (this)
            {
                Load();
                return ImmutableList.CreateRange(_profiles);
            }
        }
    }

    public void Mutate(ListMutation<ScanProfile> mutation, ISelectable<ScanProfile> selectable)
    {
        mutation.Apply(_profiles, selectable);
        Save();
    }

    public void Mutate(ListMutation<ScanProfile> mutation, ListSelection<ScanProfile> selection)
    {
        mutation.Apply(_profiles, ref selection);
        Save();
    }

    public ScanProfile? DefaultProfile
    {
        get
        {
            lock (this)
            {
                Load();
                if (_profiles.Count == 1)
                {
                    return _profiles.First();
                }
                return _profiles.FirstOrDefault(x => x.IsDefault);
            }
        }
        set
        {
            lock (this)
            {
                Load();
                foreach (var profile in _profiles)
                {
                    profile.IsDefault = false;
                }
                value.IsDefault = true;
                Save();
            }
        }
    }

    public void Load()
    {
        lock (this)
        {
            if (_profiles != null)
            {
                return;
            }
            _profiles = GetProfiles();
        }
    }

    public void Save()
    {
        lock (this)
        {
            _userScope.Set(c => c.Profiles = ImmutableList.CreateRange(_profiles));
        }
        ProfilesUpdated?.Invoke(this, EventArgs.Empty);
    }

    private List<ScanProfile> GetProfiles()
    {
        var userProfiles = (_userScope.Get(c => c.Profiles) ?? ImmutableList<ScanProfile>.Empty).ToList();
        var systemProfiles = (_appScope.Get(c => c.Profiles) ?? ImmutableList<ScanProfile>.Empty).ToList();
        if (_noUserProfiles && systemProfiles.Count > 0)
        {
            // Configured by administrator to only use system profiles
            // But the user might still be able to change devices
            MergeUserProfilesIntoSystemProfiles(userProfiles, systemProfiles);
            return systemProfiles;
        }
        if (!_userPathExisted)
        {
            // Initialize with system profiles since it's a new user
            return systemProfiles;
        }
        if (!_lockSystemProfiles)
        {
            // Ignore the system profiles since the user already has their own
            return userProfiles;
        }
        // LockSystemProfiles has been specified, so we need both user and system profiles.
        MergeUserProfilesIntoSystemProfiles(userProfiles, systemProfiles);
        if (userProfiles.Any(x => x.IsDefault))
        {
            foreach (var systemProfile in systemProfiles)
            {
                systemProfile.IsDefault = false;
            }
        }
        return systemProfiles.Concat(userProfiles).ToList();
    }

    private void MergeUserProfilesIntoSystemProfiles(List<ScanProfile> userProfiles, List<ScanProfile> systemProfiles)
    {
        foreach (var systemProfile in systemProfiles)
        {
            systemProfile.IsLocked = true;
            systemProfile.IsDeviceLocked = (systemProfile.Device != null || _lockUnspecifiedDevices);
        }

        var systemProfileNames = new HashSet<string>(systemProfiles.Select(x => x.DisplayName));
        foreach (var profile in userProfiles)
        {
            if (systemProfileNames.Contains(profile.DisplayName))
            {
                // Merge some properties from the user's copy of the profile
                var systemProfile = systemProfiles.First(x => x.DisplayName == profile.DisplayName);
                if (systemProfile.Device == null)
                {
                    systemProfile.Device = profile.Device;
                }

                systemProfile.IsDefault = profile.IsDefault;

                // Delete the user's copy of the profile
                userProfiles.Remove(profile);
                // Avoid removing duplicates
                systemProfileNames.Remove(profile.DisplayName);
            }
        }
    }

    private class ProfileConfig
    {
        public ImmutableList<ScanProfile> Profiles { get; set; }
    }

    private class ProfileSerializer : ISerializer<ProfileConfig>
    {
        private readonly XmlSerializer<ImmutableList<ScanProfile>> _internalSerializer = new XmlSerializer<ImmutableList<ScanProfile>>();

        public void Serialize(Stream stream, ProfileConfig obj) => _internalSerializer.Serialize(stream, obj.Profiles);

        public ProfileConfig Deserialize(Stream stream) => new ProfileConfig { Profiles = _internalSerializer.Deserialize(stream) };
    }
}