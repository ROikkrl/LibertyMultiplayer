using System;
using System.Collections.Generic;
using GTA;

public sealed partial class LibertyFriends
{
    private sealed class VisualClip
    {
        public readonly AnimationSet Animations;
        public readonly string Group, Name;
        public readonly bool UpperBody, Loop;

        public VisualClip(string group, string name, bool upperBody = false, bool loop = false)
        {
            Group = group;
            Animations = new AnimationSet(group);
            Name = name;
            UpperBody = upperBody;
            Loop = loop;
        }
    }

    private static readonly string[] GunGroups = {
        null, null, null, null, "throw_grenade", "throw_grenade", null, "gun@handgun", null, "gun@deagle",
        "gun@shotgun", "gun@baretta", "gun@uzi", "gun@mp5k", "gun@ak47", "gun@rifle", "gun@rifle", "gun@rifle",
        "gun@rocket"
    };
    private static readonly VisualClip[] VisualClips = MakeVisualClips();
    private static readonly AnimationSet Crouching = new AnimationSet("move_crouch");
    private static readonly AnimationSet ArmedCrouching = new AnimationSet("move_crouch_rifle");
    private static readonly AnimationSet MeleeMoving = new AnimationSet("move_melee");
    private static readonly AnimationSet AimingMoving = new AnimationSet("move_combat_strafe");
    private static readonly AnimationSet Swimming = new AnimationSet("swimming");
    private readonly GTA.Native.Pointer animationTime = new GTA.Native.Pointer(typeof(float));
    private long nextVisualRead;
    private int lastVisualClip, lastVisualPhase, visualSerial;
    private int lastMeleeSerial = -1;
    private long lastMeleeAt, jumpSeenAt;
    private uint localVisual;

    private static VisualClip[] MakeVisualClips()
    {
        var clips = new List<VisualClip> { null };
        string[] guns = { "gun@handgun", "gun@deagle", "gun@shotgun", "gun@baretta", "gun@uzi",
            "gun@mp5k", "gun@ak47", "gun@rifle", "gun@rocket" };
        foreach (string gun in guns)
        {
            foreach (string action in new[] { "fire_crouch", "fire", "reload_crouch", "reload", "holster_2_aim", "dbfire", "dbfire_l" })
                clips.Add(new VisualClip(gun, action, true));
        }
        foreach (string action in new[] { "hook_l", "hook_r", "hook_l_long", "hook_r_long", "uppercut_l",
            "uppercut_r", "elbow_l", "elbow_r", "knee", "low_kick_r_long", "headbutt", "lowblow_l", "lowblow_r" })
            clips.Add(new VisualClip("melee_player_unarmed", action));
        foreach (string action in new[] { "hook_l", "hook_r", "low_kick_r", "ground_attack_a", "block" })
            clips.Add(new VisualClip("melee_baseball_core", action));
        foreach (string action in new[] { "med_swipe_start_a", "med_swipe_start_b", "partial_swipe", "low_kick_r", "block" })
            clips.Add(new VisualClip("melee_knife_core", action));
        foreach (string action in new[] { "grenade_throw_crouch", "grenade_throw_underarm_stand", "grenade_throw_underarm",
            "grenade_throw_overarm", "grenade_throw_short", "light_molotov" })
            clips.Add(new VisualClip("throw_grenade", action, true));
        foreach (string jump in new[] { "jump_std", "jump_rifle" })
            foreach (string action in new[] { "jump_on_spot", "jump_takeoff_l", "jump_takeoff_r", "jump_inair_l",
                "jump_inair_r", "jump_land_l", "jump_land_r", "jump_land_roll", "jump_land_squat" })
                clips.Add(new VisualClip(jump, action, false, action.StartsWith("jump_inair", StringComparison.Ordinal)));
        foreach (string action in new[] { "ladder_climb_down_run", "ladder_climb_down", "ladder_climb_run",
            "ladder_geton_top", "ladder_getoff_top", "vault_start_standing", "waist_to_stand_unarmed", "hang_drop_unarmed" })
            clips.Add(new VisualClip("climb_std", action, false, action.StartsWith("ladder_climb", StringComparison.Ordinal)));
        foreach (string cover in new[] { "cover_l_high_centre", "cover_r_high_centre", "cover_l_low_corner", "cover_r_low_corner" })
            foreach (string action in new[] { "unarmed_idle", "pistol_idle", "rifle_idle", "pistol_peek", "rifle_peek",
                "pistol_blindfire", "rifle_blindfire" })
                clips.Add(new VisualClip(cover, action, false, action.EndsWith("idle", StringComparison.Ordinal)));
        foreach (string action in new[] { "shot", "shot_b", "pick_up_l", "pick_up_r", "idle_relaxed" })
            clips.Add(new VisualClip("mini_bowling", action, false, action == "idle_relaxed"));
        return clips.ToArray();
    }

    private void ResetPlayerVisual()
    {
        localVisual = 0;
        nextVisualRead = 0;
        lastVisualClip = lastVisualPhase = visualSerial = 0;
        lastMeleeSerial = -1;
        lastMeleeAt = jumpSeenAt = 0;
    }

    private uint CapturePlayerVisual(Ped ped, Vehicle car, long now)
    {
        if (now < nextVisualRead) return localVisual;
        nextVisualRead = now + 30;
        int weapon = Math.Max(0, Math.Min(44, (int)ped.Weapons.CurrentType));
        bool seated = Exists(car);
        bool duck = !seated && GTA.Native.Function.Call<bool>("IS_CHAR_DUCKING", ped);
        bool melee = !seated && GTA.Native.Function.Call<bool>("IS_CHAR_IN_MELEE_COMBAT", ped);
        bool swim = !seated && GTA.Native.Function.Call<bool>("IS_CHAR_SWIMMING", ped);
        bool aim = GTA.Native.Function.Call<bool>("IS_PLAYER_TARGETTING_ANYTHING", Player.ID);
        string gun = weapon < GunGroups.Length ? GunGroups[weapon] : "gun@rifle";
        string handCombat = !melee ? null : weapon == 3 ? "melee_knife_core" : weapon == 1 || weapon == 2
            ? "melee_baseball_core" : "melee_player_unarmed";
        bool inCover = !seated && GTA.Native.Function.Call<bool>("IS_PED_IN_COVER", ped);
        bool climbing = !seated && GTA.Native.Function.Call<bool>("IS_PED_CLIMBING", ped);
        if (!seated && GTA.Native.Function.Call<bool>("IS_CHAR_IN_AIR", ped)) jumpSeenAt = now;
        bool jump = !seated && jumpSeenAt != 0 && now - jumpSeenAt < 550;
        int index = 0, phase = 0;
        if (!ped.isRagdoll && ped.isAlive)
        {
            for (int i = 1; i < VisualClips.Length; ++i)
            {
                VisualClip clip = VisualClips[i];
                bool relevant = clip.Group == gun || clip.Group == handCombat || !seated &&
                    (jump && (clip.Group == "jump_std" || clip.Group == "jump_rifle") ||
                     climbing && clip.Group == "climb_std" || clip.Group == "mini_bowling" ||
                     inCover && clip.Group.StartsWith("cover_", StringComparison.Ordinal));
                if (!relevant) continue;
                if (!clip.Animations.isInMemory || !clip.Animations.isPedPlayingAnimation(ped, clip.Name)) continue;
                GTA.Native.Function.Call("GET_CHAR_ANIM_CURRENT_TIME", ped, clip.Group, clip.Name, animationTime);
                float progress = (float)animationTime.Value;
                index = i;
                phase = progress >= 0 && progress <= 1 ? (int)Math.Round(progress * 127f) : 0;
                break;
            }
        }
        if (index != lastVisualClip || (index != 0 && phase + 22 < lastVisualPhase)) visualSerial = (visualSerial + 1) & 127;
        lastVisualClip = index;
        lastVisualPhase = phase;
        int flags = (duck ? 1 : 0) | (aim ? 2 : 0) | (melee ? 4 : 0) | (swim ? 8 : 0);
        localVisual = (uint)(flags | weapon << 4 | index << 10 | phase << 18 | visualSerial << 25);
        return localVisual;
    }

    private bool IsMeleeContact(Ped attacker, Ped avatar, long now)
    {
        int clip = lastVisualClip;
        bool swing = clip > 0 && VisualClips[clip].Group.StartsWith("melee_", StringComparison.Ordinal) &&
            VisualClips[clip].Name != "block";
        if (now - lastMeleeAt < 360 && (!swing || lastMeleeSerial == visualSerial)) return false;
        if (avatar.HasBeenDamagedBy(attacker)) return true;
        return swing && lastVisualPhase >= 38 && lastVisualPhase <= 85 &&
            (lastMeleeSerial != visualSerial || now - lastMeleeAt >= 1100) &&
            SquaredDistance(attacker.Position, avatar.Position) < 1.85f * 1.85f &&
            GTA.Native.Function.Call<bool>("IS_PLAYER_TARGETTING_CHAR", Player.ID, avatar);
    }

    private void ApplyPlayerVisual(Ped avatar, Peer peer, AvatarGait state, int motion, long now, bool seated)
    {
        uint visual = peer.Animation;
        int flags = (int)(visual & 15), weapon = (int)(visual >> 4) & 63;
        int clipId = (int)(visual >> 10) & 255, phase = (int)(visual >> 18) & 127, serial = (int)(visual >> 25);
        if (weapon > 44) weapon = 0;
        if (weapon != state.Weapon)
        {
            avatar.Weapons.RemoveAll();
            if (weapon > 0 && weapon != 6 && weapon != 8)
            {
                GTA.Native.Function.Call("GIVE_WEAPON_TO_CHAR", avatar, weapon, 1, 0);
                avatar.Weapons.Select((Weapon)weapon);
            }
            state.Weapon = weapon;
        }
        VisualClip action = (clipId > 0 && clipId < VisualClips.Length) ? VisualClips[clipId] : null;
        if (seated && action != null && !action.UpperBody) action = null;
        if (!seated && (action == null || action.UpperBody))
        {
            bool duck = (flags & 1) != 0, aim = (flags & 2) != 0;
            AnimationSet movement = (flags & 8) != 0 ? Swimming : duck ? (weapon >= 10 ? ArmedCrouching : Crouching)
                : (flags & 4) != 0 ? MeleeMoving : aim && weapon >= 7 ? AimingMoving : playerMovement;
            if (movement != playerMovement && motion == 3 && movement != Swimming) motion = 2;
            string name = gaitClips[motion];
            if (motion != 0 && motion != 3 && !duck && movement != Swimming && peer.Velocity.HasValue)
            {
                Vector3 velocity = peer.Velocity.Value, direction = avatar.Direction;
                float forward = direction.X * velocity.X + direction.Y * velocity.Y;
                float lateral = direction.Y * velocity.X - direction.X * velocity.Y;
                if (Math.Abs(lateral) > Math.Abs(forward) * 1.15f) name += lateral < 0 ? "_strafe_l" : "_strafe_r";
                else if (forward < -0.6f) name += "_strafe_b";
            }
            bool changed = state.Movement != movement || state.MovementClip != name || state.ActionClip > 0 && !state.ActionUpper;
            if (changed || now >= state.CheckAt)
            {
                state.CheckAt = now + 550;
                if (changed || !movement.isInMemory || !movement.isPedPlayingAnimation(avatar, name))
                    avatar.Task.PlayAnimation(movement, name, 2f, AnimationFlags.Unknown01);
                state.Movement = movement;
                state.MovementClip = name;
            }
            state.Clip = motion;
        }
        if (action == null)
        {
            if (state.ActionClip != 0 && state.ActionUpper) GTA.Native.Function.Call("CLEAR_CHAR_SECONDARY_TASK", avatar);
            state.ActionClip = 0;
            return;
        }
        if (!action.Animations.isInMemory)
        {
            if (now >= state.ActionCheckAt)
            {
                GTA.Native.Function.Call("REQUEST_ANIMS", action.Group);
                state.ActionCheckAt = now + 150;
            }
            return;
        }
        bool start = state.ActionClip != clipId || state.ActionSerial != serial;
        if (!start && now < state.ActionCheckAt) return;
        state.ActionCheckAt = now + 200;
        if (start || (action.Loop && !action.Animations.isPedPlayingAnimation(avatar, action.Name)))
        {
            if (action.UpperBody)
                GTA.Native.Function.Call(seated ? "TASK_PLAY_ANIM_SECONDARY_IN_CAR" : "TASK_PLAY_ANIM_SECONDARY_UPPER_BODY",
                    avatar, action.Name, action.Group, 4f, 0, 0, 0, 0, -1);
            else avatar.Task.PlayAnimation(action.Animations, action.Name, 3f,
                action.Loop ? AnimationFlags.Unknown01 : AnimationFlags.None);
        }
        if (action.Animations.isPedPlayingAnimation(avatar, action.Name))
            GTA.Native.Function.Call("SET_CHAR_ANIM_CURRENT_TIME", avatar, action.Group, action.Name, phase / 127f);
        state.ActionClip = clipId;
        state.ActionSerial = serial;
        state.ActionUpper = action.UpperBody;
    }
}
