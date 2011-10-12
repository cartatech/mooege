﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mooege.Net.GS.Message.Fields;
using Mooege.Core.GS.Actors;
using Mooege.Net.GS;
using Mooege.Net.GS.Message.Definitions.Combat;
using Mooege.Core.GS.Skills;
using Mooege.Net.GS.Message.Definitions.ACD;
using Mooege.Net.GS.Message.Definitions.Misc;
using Mooege.Common.Helpers;
using Mooege.Net.GS.Message.Definitions.Animation;
using Mooege.Net.GS.Message;
using Mooege.Net.GS.Message.Definitions.Effect;
using Mooege.Net.GS.Message.Definitions.Attribute;
using Mooege.Net.GS.Message.Definitions.Player;
using Mooege.Common;
using Mooege.Core.GS.Map;
using Mooege.Net.GS.Message.Definitions.World;

namespace Mooege.Core.GS.Powers
{
    public class Effect : Actor
    {
        public override ActorType ActorType { get { return Actors.ActorType.Monster; } }

        public Effect(Map.World world, int actorSNO, Vector3D position, float angle, int timeout)
            : base(world, world.NewActorID)
        {
            this.ActorSNO = actorSNO;
            RotationAmount = (float)Math.Cos(angle / 2f);
            RotationAxis = new Vector3D(0, 0, (float)Math.Sin(angle / 2f));

            // FIXME: This is hardcoded crap
            this.Field2 = 0x8;
            this.Field3 = 0x0;
            this.Scale = 1.35f;
            this.Position.Set(position);
            this.GBHandle.Type = 1; this.GBHandle.GBID = 1; // TODO: use proper enum value

            Timeout = DateTime.Now.AddMilliseconds(timeout);

            world.Enter(this);
        }

        public DateTime Timeout;
    }

    public class PowersManager
    {
        static readonly Logger Logger = LogManager.CreateLogger();

        // temporary testing helper
        private PowersMobTester _mobtester;

        private Game.Game _game;

        private List<Effect> _effects = new List<Effect>();
        private Random _rand = new Random();

        // tracking information for currently channel-casting actors
        class ChanneledCast
        {
            public DateTime CastDelay;
            public IList<Effect> Effects;
            public int CastDelayAmount;
        }
        private Dictionary<Actor, ChanneledCast> _channelingActors = new Dictionary<Actor, ChanneledCast>();

        // list of all waiting to execute powers
        class WaitingPower
        {
            public IEnumerator<int> PowerEnumerator;
            public DateTime Timeout;
            public Actor User;
        }
        List<WaitingPower> _waitingPowers = new List<WaitingPower>();

        // supplies Powers with all available targets.
        private IEnumerable<Actor> Targets
        {
            get
            {
                return PowersMobTester.AllMobs;
            }
        }

        public PowersManager(Game.Game game)
        {
            _mobtester = new PowersMobTester();
            _game = game;
        }

        public void Tick()
        {
            UpdateWaitingPowers();
            CleanUpEffects(); 
        }

        public void UsePower(Actor user, int powerId, int targetId = -1, Vector3D targetPos = null, TargetMessage message = null)
        {
            Actor target;

            if (targetId == -1)
            {
                target = null;
            }
            else if (GetActorFromId(targetId) != null)
            {
                target = GetActorFromId(targetId);
                targetPos = target.Position;
            }
            else
            {
                return;
            }

            if (targetPos == null)
                targetPos = new Vector3D(0, 0, 0);

            if (powerId == Skills.Skills.Monk.SpiritSpenders.BlindingFlash) // HACK: intercepted to use for spawning test mobs
            {
                _mobtester.SpawnMob(user);
            }
            else
            {
                // find and run a power implementation
                var implementation = PowerImplementation.ImplementationForId(powerId);
                if (implementation != null)
                {
                    // process channeled skill params
                    bool userIsChanneling = false;
                    bool throttledCast = false;
                    if (_channelingActors.ContainsKey(user))
                    {
                        userIsChanneling = true;
                        if (DateTime.Now > _channelingActors[user].CastDelay)
                        {
                            _channelingActors[user].CastDelay = DateTime.Now.AddMilliseconds(_channelingActors[user].CastDelayAmount);
                        }
                        else
                        {
                            throttledCast = true;
                        }
                    }

                    IEnumerable<int> powerExe = implementation.Run(new PowerParameters
                    {
                        User = user,
                        Target = target,
                        TargetPosition = targetPos,
                        Message = message,
                        UserIsChanneling = userIsChanneling,
                        ThrottledCast = throttledCast,
                    },
                    this);

                    var powerEnum = powerExe.GetEnumerator();
                    // actual power will first run here, if it yielded a value process it in the waiting list
                    if (powerEnum.MoveNext())
                    {
                        AddWaitingPower(_waitingPowers, powerEnum, user);
                    }
                    // send tick after executing power
                    SendDWordTickFor(user.World);
                }
            }                
        }

        private void AddWaitingPower(IList<WaitingPower> list, IEnumerator<int> powerEnum, Actor user)
        {
            WaitingPower wait = new WaitingPower();
            wait.PowerEnumerator = powerEnum;
            wait.User = user;
            if (powerEnum.Current == 0)
                wait.Timeout = DateTime.MinValue;
            else
                wait.Timeout = DateTime.Now.AddMilliseconds(powerEnum.Current);

            list.Add(wait);
        }

        private void UpdateWaitingPowers()
        {
            List<WaitingPower> newWaitList = new List<WaitingPower>();
            foreach (WaitingPower wait in _waitingPowers)
            {
                if (DateTime.Now > wait.Timeout)
                {
                    if (wait.PowerEnumerator.MoveNext())
                    {
                        // re-add with new timeout
                        AddWaitingPower(newWaitList, wait.PowerEnumerator, wait.User);
                    }
                    // else did not request another wait

                    // send dword tick after every power execute
                    SendDWordTickFor(wait.User.World);
                }
                else
                {
                    // re-add with same timeout
                    newWaitList.Add(wait);
                }
            }
            _waitingPowers = newWaitList;
        }

        public void RegisterChannelingPower(Actor user, int castDelayAmount = 0)
        {
            if (!_channelingActors.ContainsKey(user))
            {
                _channelingActors.Add(user, new ChanneledCast
                {
                    CastDelay = DateTime.Now.AddMilliseconds(castDelayAmount),
                    CastDelayAmount = castDelayAmount,
                    Effects = null,
                });
            }
        }

        public void CancelChanneledPower(Actor user, int powerId)
        {
            if (_channelingActors.ContainsKey(user))
            {
                if (_channelingActors[user].Effects != null)
                {
                    foreach (Effect effect in _channelingActors[user].Effects)
                        KillSpawnedEffect(effect);
                }
                _channelingActors.Remove(user);
            }
        }

        public void PlayEffect(int effectId, Actor target)
        {
            if (target == null) return;

            target.World.BroadcastIfRevealed(new PlayEffectMessage
            {
                Id = 0x7a,
                ActorID = target.DynamicID,
                Field1 = effectId,
                Field2 = 0x0, // TODO: figure out what this is: 0x2,
            }, target);
        }

        public void PlayHitEffect(int effectId, Actor from, Actor target)
        {
            if (target == null) return;

            target.World.BroadcastIfRevealed(new PlayHitEffectMessage
            {
                Id = 0x7b,
                ActorID = target.DynamicID,
                HitDealer = from.DynamicID,
                Field2 = effectId,
                Field3 = false
            }, target);
        }

        public void PlayRopeEffectActorToActor(int effectId, Actor from, Actor target)
        {
            if (target == null) return;

            target.World.BroadcastIfRevealed(new RopeEffectMessageACDToACD
            {
                Id = 0x00ab,
                Field0 = effectId,
                Field1 = (int)from.DynamicID,
                Field2 = 4,
                Field3 = (int)target.DynamicID,
                Field4 = 1
            }, target);
        }

        public void PlayEffectGroupActorToActor(int effectId, Actor from, Actor target)
        {
            if (target == null) return;

            target.World.BroadcastIfRevealed(new EffectGroupACDToACDMessage
            {
                Id = 0xaa,
                Field0 = effectId,
                Field1 = (int)from.DynamicID,
                Field2 = (int)target.DynamicID
            }, target);
        }

        public void ActorLookAt(Actor actor, Vector3D targetPos)
        {
            actor.World.BroadcastIfRevealed(new ACDTranslateFacingMessage
            {
                Id = 0x70,
                ActorID = actor.DynamicID,
                Angle = AngleLookAt(actor.Position, targetPos),
                Field2 = false // true/false toggles whether to smoothly animate the change or instantly do it
            }, actor);
        }

        public float AngleLookAt(Vector3D a, Vector3D b)
        {
            return (float)Math.Atan2(b.Y - a.Y, b.X - a.X);
        }

        public void DoKnockback(Actor user, Actor target, float amount)
        {
            if (target == null) return;

            // TODO: figure out how to implement with amount
            Vector3D move = new Vector3D();
            move.Z = target.Position.Z;
            move.X = target.Position.X + (target.Position.X - user.Position.X);
            move.Y = target.Position.Y + (target.Position.Y - user.Position.Y);
            MoveActorNormal(target, move);
        }

        private Effect SpawnEffectInstance(Actor from, int actorSNO, Vector3D position, float angle = -1f /*random*/, int timeout = 2000)
        {
            if (angle == -1f)
                angle = (float)(_rand.NextDouble() * (Math.PI * 2));
            
            Effect effect = new Effect(from.World, actorSNO, position, angle, timeout);
            return effect;
        }

        public void SpawnEffect(Actor from, int effectId, Vector3D position, float angle = -1f /*random*/, int timeout = 2000)
        {
            _effects.Add(SpawnEffectInstance(from, effectId, position, angle, timeout));
        }

        public void SpawnEffect(Actor from, int effectId, Vector3D position, Actor point_to_actor, int timeout = 2000)
        {
            float angle = (point_to_actor != null) ? AngleLookAt(from.Position, point_to_actor.Position) : -1f;
            _effects.Add(SpawnEffectInstance(from, effectId, position, angle, timeout));
        }

        public void KillSpawnedEffect(Effect effect)
        {
            var world = effect.World;
            effect.Destroy();
            SendDWordTickFor(world);
        }

        public IList<Actor> FindActorsInRange(Actor user, Vector3D center, float range, int maxCount = -1)
        {
            List<Actor> hits = new List<Actor>();
            foreach (Actor actor in user.World.GetActorsInRange(center, range))
            {
                if (hits.Count == maxCount)
                    break;

                // TODO: only work with more than just SimpleMobs
                if (actor is SimpleMob)
                    hits.Add(actor);
            }

            return hits;
        }

        public bool WillHitMeleeTarget(Actor user, Actor target)
        {
            if (target == null) return false;
            return (Math.Sqrt(
                        Math.Pow(user.Position.X - target.Position.X, 2) +
                        Math.Pow(user.Position.Y - target.Position.Y, 2)) <= 13f);
        }

        public Effect SpawnTempProxy(Actor from, Vector3D pos, int timeout = 2000)
        {
            Effect effect = SpawnEffectInstance(from, 187359, pos, 0, timeout);
            _effects.Add(effect);
            return effect;
        }

        public Effect GetChanneledProxy(Actor user, int index, Vector3D pos)
        {
            if (!_channelingActors.ContainsKey(user)) return null;

            ChanneledCast channeled = _channelingActors[user];
            if (channeled.Effects == null)
                channeled.Effects = new List<Effect>();

            // ensure effects list is at least big enough for specified index
            while (channeled.Effects.Count < index + 1)
            {
                channeled.Effects.Add(SpawnEffectInstance(user, 187359, pos, 0));
            }

            RepositionActor(channeled.Effects[index], pos);

            return channeled.Effects[index];
        }

        public void RepositionActor(Actor actor, Vector3D pos)
        {
            if (actor == null) return;
            // rely on Position setter to use WorldPositionMessage
            actor.Position = pos;
        }

        public void MoveActorNormal(Actor actor, Vector3D pos)
        {
            if (actor == null) return;

            actor.Position.Set(pos);
            actor.World.BroadcastIfRevealed(new ACDTranslateNormalMessage()
            {
                Id = 0x6e,
                Field0 = (int)actor.DynamicID,
                Position = pos,
                Angle = 0f, // TODO: convert quaternion rotation for this?
                Field3 = false,
                Field4 = 1.0f,
            }, actor);
        }

        public void DoDamage(Actor from, Actor target, float amount, int type)
        {
            if (target == null) return;

            target.World.BroadcastIfRevealed(new FloatingNumberMessage
            {
                Id = 0xd0,
                ActorID = target.DynamicID,
                Number = amount,
                Type = FloatingNumberMessage.FloatType.White//type,
            }, target);

            // TODO: handling more damagable types
            if (target is SimpleMob)
            {
                ((SimpleMob)target).ReceiveDamage(from, amount, type);
            }
        }

        public void DoDamage(Actor from, IList<Actor> target_list, float amount, int type)
        {
            foreach (Actor target in target_list)
            {
                DoDamage(from, target, amount, type);
            }
        }

        private Actor GetActorFromId(int id)
        {
            return Targets.FirstOrDefault(t => t.DynamicID == id);
        }

        private void CleanUpEffects()
        {
            List<Effect> survivors = new List<Effect>();
            var curtime = DateTime.Now;
            foreach (Effect effect in _effects)
            {
                if (curtime > effect.Timeout)
                    KillSpawnedEffect(effect);
                else
                    survivors.Add(effect);
            }

            _effects = survivors;
        }

        private void SendDWordTickFor(World world)
        {
            // HACK: just sends tick to all players in specified world
            foreach (GameClient client in world.Game.Players.Values.Where(p => p.World == world).Select(p => p.InGameClient))
            {
                client.PacketId += 10 * 2;
                client.SendMessage(new DWordDataMessage()
                {
                    Id = 0x89,
                    Field0 = client.PacketId,
                });
                client.FlushOutgoingBuffer();
            }
        }
    }
}
