﻿using BepInEx;
using RoR2;
using System.Reflection;
using UnityEngine;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using RoR2.WwiseUtils;
using System;
using System.Linq;
using System.IO;
using R2API.Utils;
using System.Collections.Generic;

namespace MultiTrackDriftingTurrets
{
	[BepInPlugin("com.prismism.multitrackdriftingturrets", MOD_NAME, "3.0.0")]
	public class MultiTrackDriftingTurrets : BaseUnityPlugin
	{
		public static PluginInfo PInfo { get; private set; }

		public const string MOD_NAME = "MultiTrackDriftingTurrets";

		// soundbank events:

		// Used to start a single instance of music on a gameobject
		private const uint DEJA_VU_TIME = 3263900111;
		// Used to stop all music instances
		private const uint DEJA_VU_TIME_STOPS = 4064506877;
		// Used to pause a single music instance on a gameobject
		private const uint DEJA_VU_TIME_PAUSES = 1412652099;
		// Used to resume a single music instance on a gameobject
		private const uint DEJA_VU_TIME_CONTINUES = 1519326796;

		private static List<TurretStatus> EngiTurretsToTrack = new List<TurretStatus>();


		//The Awake() method is run at the very start when the game is initialized.
		public void Awake()
		{
			Log.Init(Logger);
			PInfo = Info;

			On.RoR2.Stage.Start += Stage_Start;

			On.EntityStates.Turret1.SpawnState.OnEnter += SpawnState_OnEnter;

			// UNCOMMENT THIS FOR MULTIPLAYER TESTING USING SEVERAL LOCAL GAME INSTANCES
			//On.RoR2.Networking.GameNetworkManager.OnClientConnect += GameNetworkManager_OnClientConnect1;
		}

		public void Start()
		{
			SoundBank.Init();
		}

		// UNCOMMENT THIS FOR MULTIPLAYER TESTING USING SEVERAL LOCAL GAME INSTANCES
		//private void GameNetworkManager_OnClientConnect1(On.RoR2.Networking.GameNetworkManager.orig_OnClientConnect orig, RoR2.Networking.GameNetworkManager self, UnityEngine.Networking.NetworkConnection conn)
		//{
		//    // Do nothing
		//}

		// EXPERIMENTAL
		private void SpawnState_OnEnter(On.EntityStates.Turret1.SpawnState.orig_OnEnter orig, EntityStates.Turret1.SpawnState self)
		{
			orig(self);

			// Get the number of goat-hooves
			// Retrieve it from the inventory of the engineer that spawned the turret
			// because clients do not track inventory for the turret itself (server only)
			// but they do track player inventories.
			var body = self.outer.GetComponent<CharacterBody>();
			var inventory = body.master.minionOwnership.ownerMaster.inventory;

			int hooves = inventory.GetItemCount(RoR2Content.Items.Hoof.itemIndex);

			// "Start" the music, though we expect it to be immediately paused
			// since the turret is stationary when it spawns.
			AkSoundEngine.PostEvent(DEJA_VU_TIME, self.outer.gameObject);

			// Register the turret to be tracked by the plugin
			EngiTurretsToTrack.Add(new TurretStatus(self.outer.gameObject, hooves, true));
		}

		// I noticed that going between stages (especially when looping back
		// to stages you've been to before, you'd hear orphan deja vu instances
		// floating in space. This performs orphancide.
		private void Stage_Start(On.RoR2.Stage.orig_Start orig, Stage self)
		{
			orig(self);

			AkSoundEngine.PostEvent(DEJA_VU_TIME_STOPS, null);
		}

		public void FixedUpdate()
		{
			// To avoid trying to remove while iterating through our list
			List<TurretStatus> toRemove = new List<TurretStatus>();

			// I don't know 100% if this is needed, but I started getting
			// fewer errors with it in.
			int preRemoved = EngiTurretsToTrack.RemoveAll((status) => status.TurretGameObject == null);

			foreach (var status in EngiTurretsToTrack)
			{
				if (status.TurretGameObject is null || !status.TurretGameObject.activeInHierarchy)
				{
					toRemove.Add(status);
					continue;
				}

				float speed = status.Speed;

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
				float newVolumeModifier = status.NumSpeedItems * 0.25f + speed / 20 + ((float)Math.Sqrt(status.NumSpeedItems) * speed / 22) - 1;
				RtpcSetter gameParamSetter = new RtpcSetter("Speeds", status.TurretGameObject) { value = newVolumeModifier };
				gameParamSetter.FlushIfChanged();

				if (speed <= 0.0001 && status.MusicPlaying)
				{
					// If it's NOT moving and the music IS playing, then PAUSE
					status.MusicPlaying = false;
					AkSoundEngine.PostEvent(DEJA_VU_TIME_PAUSES, status.TurretGameObject);
				}
				else if (speed > 0.0001 && !status.MusicPlaying)
				{
					// If it IS moving and the music is NOT playing, then RESUME
					status.MusicPlaying = true;
					AkSoundEngine.PostEvent(DEJA_VU_TIME_CONTINUES, status.TurretGameObject);
				}

				// The last thing we want to do, prep for next update
				status.RecordLastPosition();
			}

			// TESTING ONLY
			//if (Input.GetKeyDown(KeyCode.F2))
			//{
			//	// Get the player body to use a position:
			//	var transform = PlayerCharacterMasterController.instances[0].master.GetBodyObject().transform;

			//	// And then drop our defined item in front of the player.

			//	//Log.Info($"Player pressed F2. Spawning hoof at coordinates {transform.position}");
			//	RoR2.PickupDropletController.CreatePickupDroplet(RoR2.PickupCatalog.FindPickupIndex(RoR2Content.Items.Hoof.itemIndex), transform.position, transform.forward * 20f);
			//}
		}
	}

	/// <summary>
	/// Just a convenient way of storing some basic info
	/// about a turret that the plugin cares about
	/// </summary>
	public class TurretStatus
	{
		public GameObject TurretGameObject { get; set; }
		public int NumSpeedItems { get; set; }
		public bool MusicPlaying { get; set; }

		private Vector3 LastPosition { get; set; }

		public float Speed
		{
			get
			{
				if (LastPosition != null && TurretGameObject != null)
				{
					Vector3 diff = TurretGameObject.transform.position - LastPosition;
					return diff.magnitude / Time.fixedDeltaTime;
				}

				return 0;
			}
		}

		public TurretStatus(GameObject gameObj, int numSpeedItems, bool musicPlaying)
		{
			TurretGameObject = gameObj;
			NumSpeedItems = numSpeedItems;
			MusicPlaying = musicPlaying;
		}

		public void RecordLastPosition()
		{
			if (TurretGameObject != null)
			{
				LastPosition = TurretGameObject.transform.position;
			}
		}
	}

}