using CameraUtils.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using Zenject;

namespace FocusMod {
    class FocusMod : IInitializable, ITickable {
		const int HiddenHudLayer = 23;
		const int NormalHudLayer = 5;

		GameObject[] elementsToHide;

		readonly AudioTimeSyncController audioTimeSyncController = null;
		readonly BeatmapLevel beatmapLevel = null;
		readonly BeatmapKey beatmapKey;
        readonly IReadonlyBeatmapData beatmapData = null;

        public FocusMod(AudioTimeSyncController audioTimeSyncController, BeatmapLevel beatmapLevel, BeatmapKey beatmapKey, IReadonlyBeatmapData beatmapData) {
			this.audioTimeSyncController = audioTimeSyncController;
			this.beatmapLevel = beatmapLevel;
			this.beatmapKey = beatmapKey;
			this.beatmapData = beatmapData;
		}

		static MethodBase ScoreSaber_playbackEnabled =
			IPA.Loader.PluginManager.GetPluginFromId("ScoreSaber")?
			.Assembly.GetType("ScoreSaber.Core.ReplaySystem.HarmonyPatches.PatchHandleHMDUnmounted")?
			.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);

		struct SafeTimespan {
			public float start;
			public float end;

			public SafeTimespan(float start, float end) {
				this.start = start;
				this.end = end;
			}
		}

		int lastTimespanIndex = 0;
		SafeTimespan[] visibleTimespans;

		void getElements() {
			var counterHuds = UnityEngine.Object.FindObjectsOfType<Canvas>()
				.Where(xd => xd.transform.childCount != 0 && xd.name.StartsWith("Counters+ | ", StringComparison.Ordinal))
				.Select(x => x.gameObject)
				.ToArray();

			var _scoreElement =
				UnityEngine.Object.FindObjectOfType<ImmediateRankUIPanel>()?.gameObject ??
				UnityEngine.Object.FindObjectOfType<ScoreUIController>()?.gameObject;

			if(!PluginConfig.Instance.HideAll) {
				if(!_scoreElement.GetComponent<Canvas>())
					_scoreElement.AddComponent<Canvas>();

				elementsToHide = new GameObject[] { _scoreElement };
			} else {
				elementsToHide = new GameObject[] {
					_scoreElement,
					UnityEngine.Object.FindObjectOfType<ScoreMultiplierUIController>()?.gameObject
				}
				.Concat(counterHuds)
				.Concat(
					// Combo panel has sub canvas' which would not hide otherwise
					UnityEngine.Object.FindObjectOfType<ComboUIController>()?.GetComponentsInChildren<Canvas>().Select(x => x.gameObject) ?? new GameObject[] { }
				).ToArray();
			}

			elementsToHide = elementsToHide.Where(x => x?.activeSelf == true).ToArray();
		}

		void ParseMap(IReadonlyBeatmapData beatmapData) {
			float lastObjectTime = 0f;

			var visibleTimespans = new List<SafeTimespan>();

			void CheckAndAdd(float objectTime, bool isLast = false) {
				if(isLast || (objectTime - lastObjectTime - PluginConfig.Instance.LeadTime >= PluginConfig.Instance.MinimumDisplaytime))
					visibleTimespans.Add(new SafeTimespan(
						lastObjectTime,
						isLast ? objectTime : objectTime - PluginConfig.Instance.LeadTime
					));

				lastObjectTime = objectTime;
			}

			foreach(var beatmapObject in beatmapData.allBeatmapDataItems) {
				if(beatmapObject.type != BeatmapDataItem.BeatmapDataItemType.BeatmapObject)
					continue;

				if(lastObjectTime == beatmapObject.time)
					continue;

				if(beatmapObject is ObstacleData obs) {
					if(PluginConfig.Instance.IgnoreWalls)
						continue;

					if(obs.width == 1 && (obs.lineIndex == 0 || obs.lineIndex == 3))
						continue;
				} else if(beatmapObject is SliderData sld) {
					if(sld.sliderType == SliderData.Type.Normal)
						continue;

					CheckAndAdd(Math.Max(sld.tailTime, sld.time));
				} else {
					if(PluginConfig.Instance.IgnoreBombs && (beatmapObject as NoteData)?.gameplayType == NoteData.GameplayType.Bomb)
						continue;

					CheckAndAdd(beatmapObject.time);
				}
			}

			CheckAndAdd(audioTimeSyncController.songLength, true);

			this.visibleTimespans = visibleTimespans.ToArray();
		}


		public void Initialize() {
			if(PluginConfig.Instance.LeadTime == 0f)
				return;

			try {
				if(ScoreSaber_playbackEnabled != null && !(bool)ScoreSaber_playbackEnabled.Invoke(null, null))
					return;
			} catch { }

            var info = beatmapLevel.GetDifficultyBeatmapData(beatmapKey.beatmapCharacteristic, beatmapKey.difficulty);
            var njs = BeatmapDifficultyMethods.NoteJumpMovementSpeed(beatmapKey.difficulty, info.noteJumpMovementSpeed, false);

            if (njs < PluginConfig.Instance.MinimumNjs)
				return;

			ParseMap(beatmapData);

            _ = InitStuff();
        }
        async Task InitStuff() {
			await Task.Yield();
            getElements();

			int HudToggle(int flag, bool show = true) => show ? flag | 1 << HiddenHudLayer : flag & ~(1 << HiddenHudLayer);

			foreach(var cam in Resources.FindObjectsOfTypeAll<Camera>()) {
				if(!PluginConfig.Instance.HideOnlyInHMD || cam.name == "MainCamera") {
					cam.cullingMask = HudToggle(cam.cullingMask, false);
				} else {
					cam.cullingMask = HudToggle(cam.cullingMask, (cam.cullingMask & (1 << NormalHudLayer)) != 0);
				}
			}

			var LIVTHING = UnityEngine.Object.FindObjectOfType<LIV.SDK.Unity.LIV>();

			if(LIVTHING != null)
				LIVTHING.spectatorLayerMask = HudToggle(LIVTHING.spectatorLayerMask, PluginConfig.Instance.HideOnlyInHMD);

#if DEBUG
			Plugin.Log.Notice("Safe timespans in this song:");

			foreach(var x in visibleTimespans)
				Plugin.Log.Notice(string.Format("{0} - {1}", x.start, x.end));

			Plugin.Log.Notice(string.Format("Elements to hide: {0}", string.Join(", ", elementsToHide.Select(x => x.name))));
#endif
		}

		bool isVisible = true;

		private void SetHudVisibility(bool visible) {
            if (isVisible == visible)
				return;

            isVisible = visible;

			foreach(var elem in elementsToHide)
			{
				if (visible)
				{
                    VisibilityUtils.SetLayerRecursively(elem, VisibilityLayer.UI);
                }
				else
				{
                    VisibilityUtils.SetLayerRecursively(elem, VisibilityLayer.DesktopOnlyAndReflected);
                }
            }
		}

		public void Tick() {
			if(elementsToHide == null)
				return;

			var isPaused = audioTimeSyncController.state != AudioTimeSyncController.State.Playing;

			if(isPaused && isVisible)
				return;

            if (lastTimespanIndex >= visibleTimespans.Length)
				return;

            // If something rewound the time back we need to find a new start index
            if (lastTimespanIndex != 0 && audioTimeSyncController.songTime < visibleTimespans[lastTimespanIndex - 1].end)
				lastTimespanIndex = 0;

			var intendedVisibility = false;

			for(var i = lastTimespanIndex; i < visibleTimespans.Length; i++) {
				var ts = visibleTimespans[i];

				if(ts.start > audioTimeSyncController.songTime)
					break;

				if(ts.end < audioTimeSyncController.songTime) {
					lastTimespanIndex++;
					continue;
				}

				if(ts.start < audioTimeSyncController.songTime) {
					intendedVisibility = true;
					break;
				}
			}

            SetHudVisibility(intendedVisibility || (isPaused && PluginConfig.Instance.UnhideInPause));
		}
	}
}
