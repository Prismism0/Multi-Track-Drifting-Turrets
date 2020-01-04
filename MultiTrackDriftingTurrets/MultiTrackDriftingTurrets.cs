using BepInEx;
using RoR2;
using System.Reflection;
using UnityEngine;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using R2API.AssetPlus;
using RoR2.WwiseUtils;
using System;
using System.Linq;
using System.IO;
using R2API.Utils;
using System.Collections.Generic;
using Hj;

namespace MultiTrackDriftingTurrets
{
    [BepInDependency(R2API.R2API.PluginGUID, BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency(Hj.HjUpdaterAPI.GUID, BepInDependency.DependencyFlags.SoftDependency)]
    [R2APISubmoduleDependency(nameof(AssetPlus))]
    [BepInPlugin("com.prismism.multitrackdriftingturrets", MOD_NAME, "1.1.0")]
    public class MultiTrackDriftingTurrets : BaseUnityPlugin
    {
        public const string MOD_NAME = "MultiTrackDriftingTurrets";


        private const string BankName = "DEJAVU_Soundbank.bnk";

        // soundbank events:

        // Used to start a single instance of music on a gameobject
        private const uint DEJA_VU_TIME = 3263900111;
        // Used to stop all music instances
        private const uint DEJA_VU_TIME_STOPS = 4064506877;
        // Used to pause a single music instance on a gameobject
        private const uint DEJA_VU_TIME_PAUSES = 1412652099;
        // Used to resume a single music instance on a gameobject
        private const uint DEJA_VU_TIME_CONTINUES = 1519326796;

        private static List<Tuple<GameObject, TurretStatus>> EngiTurretsToTrack = new List<Tuple<GameObject, TurretStatus>>();

        public static void AddSoundBank()
        {
            var soundbank = LoadEmbeddedResource(BankName);
            if (soundbank != null)
            {
                SoundBanks.Add(soundbank);
            }
            else
            {
                UnityEngine.Debug.LogError("SoundBank Fetching Failed");
            }
        }

        private static byte[] LoadEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();

            resourceName = assembly.GetManifestResourceNames()
                .Single(str => str.EndsWith(resourceName));

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            using (var reader = new BinaryReader(stream ?? throw new InvalidOperationException()))
            {
                return reader.ReadBytes(Convert.ToInt32(stream.Length.ToString()));
            }

        }

        /// <summary>
        /// Static function to contain this HjUpdaterAPI line, because
        /// it doesn't work otherwise. Intuitive!
        /// </summary>
        private static void Updater()
        {
            Hj.HjUpdaterAPI.Register(MOD_NAME);
        }

        //The Awake() method is run at the very start when the game is initialized.
        public void Awake()
        {
            // Optional auto-update functionality
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(Hj.HjUpdaterAPI.GUID))
            {
                Updater();
            }

            //  Register the DEJA VU sample, and the events that allow us to control when it plays
            AddSoundBank();

            // Inject some logic into HandleConstructTurret which will register
            // each turret created into the 'EngiTurretsToTrack' collection 
            IL.RoR2.CharacterBody.HandleConstructTurret += (il) =>
            {
                ILCursor c = new ILCursor(il);
                c.GotoNext(
                    x => x.MatchLdcI4(0x56),
                    x => x.MatchCallvirt<Inventory>("ResetItem")
                );
                c.Index += 4;

                c.Emit(OpCodes.Ldloc_3);
                c.EmitDelegate<Action<CharacterMaster>>((charMaster) =>
                {
                    if(charMaster != null && charMaster.inventory != null)
                    {
                        var inv = charMaster.inventory;
                        GameObject soundSource = charMaster.GetBody().gameObject;

                        if (charMaster.gameObject != null)
                        {
                            // "Start" the music. We expect it to be promptly paused since
                            // the turret is stationary when it spawns. We need to call 'start'
                            // at some point and this is as good as any.
                            AkSoundEngine.PostEvent(DEJA_VU_TIME, soundSource);

                            // Register this new turret with the plugin (for processing in the plugin's update function)
                            EngiTurretsToTrack.Add(new Tuple<GameObject, TurretStatus>(soundSource, new TurretStatus(inv.GetItemCount(ItemIndex.Hoof), true)));
                            //Debug.Log($"Tracking {EngiTurretsToTrack.Count} turrets");
                        }
                    }
                });
            };

            On.RoR2.Stage.Start += Stage_Start;
        }

        // I noticed that going between stages (especially when looping back
        // to stages you've been to before, you'd hear orphan deja vu instances
        // floating in space. This performs orphancide.
        private void Stage_Start(On.RoR2.Stage.orig_Start orig, Stage self)
        {
            orig(self);

            AkSoundEngine.PostEvent(DEJA_VU_TIME_STOPS, null);
        }

        public void Update()
        {
            // To avoid trying to remove while iterating through our list
            List<Tuple<GameObject, TurretStatus>> toRemove = new List<Tuple<GameObject, TurretStatus>>();

            // I don't know 100% if this is needed, but I started getting
            // fewer errors with it in.
            int preRemoved = EngiTurretsToTrack.RemoveAll((tuple) => tuple.Item1 == null);

            foreach(var tuple in EngiTurretsToTrack)
            {
                if (tuple.Item1 is null || !tuple.Item1.activeInHierarchy)
                {
                    toRemove.Add(tuple);
                    continue;
                }

                // Get how fast the turret is moving right now
                float speed;
                var body = tuple.Item1.GetComponent<Rigidbody>();
                if(body != null)
                {
                    speed = body.velocity.magnitude;
                }
                else
                {
                    toRemove.Add(tuple);
                    continue;
                }

                // Set the volume of the music to change depending on current speed
                // Formula is this:
                //          speedItems/4 + speed/20 + sqrt(speedItems)*speed/22 - 1
                //
                // Explanation:
                //      I wanted the following things to be true:
                //          1. Nothing would be heard under normal circumstances with 0 speed items.
                //          2. Volume would scale off of a combination of speedItems and speed
                //          3. Volume would not fluctuate very noticeably as the turret slowed down/sped up.
                //
                //      After some tweaking, I ended up preferring a mix of additive and multiplicitave scaling.
                //      The -1 at the end helps increase the number of speedItems you need before you hear anything.
                float newVolumeModifier = tuple.Item2.NumSpeedItems * 0.25f + speed / 20 + ( (float)Math.Sqrt(tuple.Item2.NumSpeedItems) * speed / 22) - 1;
                RtpcSetter gameParamSetter = new RtpcSetter("Speeds", tuple.Item1) { value = newVolumeModifier };
                gameParamSetter.FlushIfChanged();

                if(speed <= 0.0001 && tuple.Item2.MusicPlaying)
                {
                    // If it's NOT moving and the music IS playing, then PAUSE
                    tuple.Item2.MusicPlaying = false;
                    AkSoundEngine.PostEvent(DEJA_VU_TIME_PAUSES, tuple.Item1);
                }
                else if(speed > 0.0001 && !tuple.Item2.MusicPlaying)
                {
                    // If it IS moving and the music is NOT playing, then RESUME
                    tuple.Item2.MusicPlaying = true;
                    AkSoundEngine.PostEvent(DEJA_VU_TIME_CONTINUES, tuple.Item1);
                }
                    
            }

            foreach (var obj in toRemove)
            {
                EngiTurretsToTrack.Remove(obj);
            }

            // Uncomment for DEBUG purposes
            /*
            if (toRemove.Any() || preRemoved > 0)
            {
                if (EngiTurretsToTrack.Any())
                {
                    Debug.Log($"Removed {toRemove.Count + preRemoved} things, and dictionary is NOT empty");
                }
                else
                {
                    Debug.Log($"Removed {toRemove.Count + preRemoved} things, and dictionary IS empty");
                }
            }
            */
        }
    }

    /// <summary>
    /// Just a convenient way of storing some basic info
    /// about a turret that the plugin cares about
    /// </summary>
    public class TurretStatus
    {
        public int NumSpeedItems { get; set; }
        public bool MusicPlaying { get; set; }

        public TurretStatus(int numSpeedItems, bool musicPlaying)
        {
            NumSpeedItems = numSpeedItems;
            MusicPlaying = musicPlaying;
        }
    }

}