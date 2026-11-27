using System;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using GTA;

public sealed partial class LibertyFriends
{
    private static readonly Model BowlingBall = new Model(unchecked((int)0x90FA92C6));
    private static readonly Model BowlingPin = new Model(unchecked((int)0xF4A206E4));
    private static readonly AnimationSet BowlingMovement = new AnimationSet("mini_bowling");
    private readonly GTA.Object[] pins = new GTA.Object[10];
    private GTA.Object ball;
    private string bowlingCommand, bowler;
    private long bowlingSequence, bowlingSendUntil, bowlingSeen, bowlingMessageAt, bowlingCharge;
    private Vector3 laneStart;
    private int laneHeading, bowlingPhase, bowlingFrame, bowlingShots, standingPins = 1023;
    private int myBowlingScore, otherBowlingScore, bowlingAim, ballAim, ballPower, ballElapsed, ballDuration;
    private int shownPins = -1;
    private bool playedThrow, bowlingToggleHeld;

    private void QueueBowling(string command, long now)
    {
        bowlingSequence = Math.Max(now, bowlingSequence + 1);
        bowlingCommand = command;
        bowlingSendUntil = now + 1400;
    }

    private bool BowlingKeyDown(Keys key)
    {
        if (key != Keys.F7 && (bowlingSeen == 0 || (key != Keys.PageUp && key != Keys.PageDown && key != Keys.Insert))) return false;
        Ped character = Player.Character;
        if (!Exists(character) || !Player.CanControlCharacter || Exists(character.CurrentVehicle)) return false;
        long now = clock.ElapsedMilliseconds;
        if (key == Keys.F7)
        {
            if (bowlingToggleHeld) return true;
            bowlingToggleHeld = true;
            if (now < bowlingSendUntil && bowlingCommand != null && bowlingCommand.StartsWith("join", StringComparison.Ordinal)) return true;
            if (bowlingSeen != 0) QueueBowling("leave", now);
            else
            {
                Vector3 floor = character.GetOffsetPosition(new Vector3(0f, 0f, -0.97f));
                QueueBowling(string.Format(CultureInfo.InvariantCulture, "join,{0},{1},{2},{3}",
                    (int)Math.Round(floor.X * 100), (int)Math.Round(floor.Y * 100), (int)Math.Round(floor.Z * 100),
                    (int)((character.Heading % 360 + 360) % 360)), now);
            }
            return true;
        }
        if (bowlingPhase != 1 || bowler != ownId) return true;
        if (key == Keys.Insert)
        {
            if (bowlingCharge == 0) bowlingCharge = now;
        }
        else bowlingAim = Math.Max(-850, Math.Min(850, bowlingAim + (key == Keys.PageUp ? 50 : -50)));
        return true;
    }

    private void BowlingKeyUp(Keys key)
    {
        if (key == Keys.F7) bowlingToggleHeld = false;
        if (key != Keys.Insert || bowlingCharge == 0) return;
        long now = clock.ElapsedMilliseconds;
        if (bowlingPhase == 1 && bowler == ownId)
        {
            int power = (int)Math.Max(1, Math.Min(100, (now - bowlingCharge) / 10));
            QueueBowling(string.Format(CultureInfo.InvariantCulture, "throw,{0},{1}", bowlingAim, power), now);
        }
        bowlingCharge = 0;
    }

    private void WriteBowling(StringBuilder frame, long now)
    {
        if (bowlingCommand != null && now < bowlingSendUntil)
            frame.AppendFormat(CultureInfo.InvariantCulture, "b,{0},{1}\n", bowlingSequence, bowlingCommand);
    }

    private void ReadBowling(string owner, string[] fields, long now)
    {
        if (fields.Length != 17 || owner != ownId && fields[6] != ownId) return;
        int x, y, z, turn, firstScore, secondScore, phase, heading;
        if (!int.TryParse(fields[1], out x) || !int.TryParse(fields[2], out y) || !int.TryParse(fields[3], out z) ||
            !int.TryParse(fields[4], out heading) || !int.TryParse(fields[5], out phase) || !int.TryParse(fields[7], out turn) ||
            !int.TryParse(fields[8], out bowlingFrame) || !int.TryParse(fields[9], out bowlingShots) ||
            !int.TryParse(fields[10], out firstScore) || !int.TryParse(fields[11], out secondScore) ||
            !int.TryParse(fields[12], out standingPins) || !int.TryParse(fields[13], out ballAim) ||
            !int.TryParse(fields[14], out ballPower) || !int.TryParse(fields[15], out ballElapsed) ||
            !int.TryParse(fields[16], out ballDuration)) return;
        if (bowlingPhase != phase)
        {
            playedThrow = false;
            bowlingMessageAt = 0;
        }
        laneStart = new Vector3(x / 100f, y / 100f, z / 100f);
        laneHeading = heading;
        bowler = turn == 0 ? owner : fields[6];
        myBowlingScore = owner == ownId ? firstScore : secondScore;
        otherBowlingScore = owner == ownId ? secondScore : firstScore;
        bowlingPhase = phase;
        bowlingSeen = now;
    }

    private Vector3 BowlingPosition(float lateral, float forward, float height)
    {
        float angle = laneHeading * (float)Math.PI / 180f;
        float sin = (float)Math.Sin(angle), cos = (float)Math.Cos(angle);
        return new Vector3(laneStart.X + lateral * cos - forward * sin, laneStart.Y + lateral * sin + forward * cos,
            laneStart.Z + height);
    }

    private void RenderBowling(Ped character, long now)
    {
        if (bowlingSeen == 0) return;
        if (now - bowlingSeen > 1500 || !serverConnected || !Exists(character))
        {
            ClearBowling();
            return;
        }
        if (now >= bowlingMessageAt)
        {
            bowlingMessageAt = now + 240;
            int power = bowlingCharge == 0 ? 0 : (int)Math.Min(100, (now - bowlingCharge) / 10);
            string action = bowlingPhase == 0 ? "Waiting for player 2 (F7 to join)" : bowlingPhase == 4 ? "Match complete" :
                bowlingPhase != 1 ? "Ball in play" : bowler != ownId ? "Friend's turn" : string.Format(CultureInfo.InvariantCulture,
                    "PgUp/PgDn aim {0:+0;-0;0} | hold Insert to roll {1}%", bowlingAim, power);
            Game.DisplayText(string.Format(CultureInfo.InvariantCulture, "Bowling {0}/3 | You {1} - {2} | {3} | F7 exit",
                bowlingFrame, myBowlingScore, otherBowlingScore, action), 340);
        }
        if (bowlingPhase == 2 && bowler == ownId && !playedThrow && bowlingShots <= 1)
        {
            playedThrow = true;
            character.Task.PlayAnimation(BowlingMovement, "shot", 2f, AnimationFlags.None);
        }
        if (bowlingPhase == 0 || bowlingPhase == 4) return;
        if (!Exists(ball) && ModelReady(BowlingBall, now))
        {
            ball = MakeBowlingProp(BowlingBall, BowlingPosition(0f, 0.7f, 0.11f), character);
            ReleaseModelRequest(BowlingBall);
        }
        if (!Exists(pins[9]) && ModelReady(BowlingPin, now))
        {
            for (int index = 0; index < pins.Length; ++index)
            {
                if (Exists(pins[index])) continue;
                int row = index >= 6 ? 3 : index >= 3 ? 2 : index >= 1 ? 1 : 0;
                int column = index - row * (row + 1) / 2;
                pins[index] = MakeBowlingProp(BowlingPin,
                    BowlingPosition((column * 2 - row) * 0.145f, 16f + row * 0.26f, 0.20f), character);
            }
            ReleaseModelRequest(BowlingPin);
            shownPins = -1;
        }
        if (shownPins != standingPins)
        {
            for (int i = 0; i < pins.Length; ++i)
                if (Exists(pins[i])) pins[i].Rotation = new Vector3((standingPins & (1 << i)) == 0 ? 85f : 0f, 0f, laneHeading);
            shownPins = standingPins;
        }
        if (Exists(ball) && ballDuration > 0)
        {
            float progress = Math.Min(1f, (ballElapsed + now - bowlingSeen) / (float)ballDuration);
            float distance = ballPower >= 20 ? 16.8f : ballPower * 0.70f;
            ball.Position = BowlingPosition(ballAim * 0.001f * progress, 0.65f + progress * distance, 0.11f);
            ball.Rotation = new Vector3(progress * distance * 540f % 360f, 0f, laneHeading);
        }
        else if (Exists(ball) && bowlingPhase == 1)
            ball.Position = BowlingPosition(bowler == ownId ? bowlingAim * 0.0004f : 0f, 0.65f, 0.11f);
    }

    private GTA.Object MakeBowlingProp(Model model, Vector3 position, Ped character)
    {
        GTA.Object prop = World.CreateObject(model, position);
        if (!Exists(prop)) return null;
        prop.Position = position;
        prop.CurrentRoom = character.CurrentRoom;
        prop.FreezePosition = true;
        GTA.Native.Function.Call("SET_OBJECT_COLLISION", prop, false);
        return prop;
    }

    private void ClearBowling()
    {
        if (Exists(ball)) ball.Delete();
        ball = null;
        for (int i = 0; i < pins.Length; ++i)
        {
            if (Exists(pins[i])) pins[i].Delete();
            pins[i] = null;
        }
        bowlingSeen = bowlingCharge = bowlingMessageAt = 0;
        bowlingPhase = bowlingAim = 0;
        shownPins = -1;
        playedThrow = false;
        bowlingToggleHeld = false;
    }
}
