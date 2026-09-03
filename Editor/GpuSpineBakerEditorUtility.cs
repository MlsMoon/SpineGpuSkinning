using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GpuSpine.Baking;
using Spine;
using Spine.Unity;
using UnityEditor;
using UnityEngine;

namespace GpuSpine.Editor {
	/// <summary>
	/// Editor-side bake orchestration: audits one SkeletonDataAsset and bakes every target skin
	/// combination (default + every single skin + the container's declared combinations) into
	/// <see cref="GpuSpineBakedEntry"/> instances on the container sub-asset. Layout: the
	/// <see cref="GpuSpineBakedData"/> container hangs off the SkeletonDataAsset via
	/// AssetDatabase.AddObjectToAsset, and every entry mesh hangs off the container, so the whole
	/// baked state is visible inside the SkeletonDataAsset's .asset. A failed audit still writes the
	/// container — report only, no meshes.
	/// <para/>
	/// The existing container is reused, so user edits to
	/// <see cref="GpuSpineBakedData.DeclaredCombos"/> survive rebakes. Saving the container re-triggers
	/// the asset postprocessor; the no-change check (source fingerprint + entry keys) then returns
	/// early without saving, which terminates the import loop.
	/// </summary>
	public static class GpuSpineBakerEditorUtility {
		/// <summary>zSpacing every entry is baked with (v1 simplification: all known skeletons use 0;
		/// a component whose SkeletonRenderer.zSpacing differs falls back to the CPU path).</summary>
		public const float BakedZSpacing = 0f;

		const string ContainerName = "GpuSpineBakedData";

		/// <summary>One resolved bake target: display name, skin names in AddSkin order, content key.</summary>
		sealed class BakeTarget {
			public string DisplayName;
			public string[] SkinNames;
			public string Key;
		}

		/// <summary>
		/// Audits and (re)bakes one SkeletonDataAsset, updating its baked data container sub-asset.
		/// Returns the container (also when the audit failed), or null when the asset is not persisted.
		/// Does nothing when the baked state is already up to date.
		/// </summary>
		public static GpuSpineBakedData Rebake (SkeletonDataAsset asset) {
			if (asset == null) throw new ArgumentNullException("asset");
			string path = AssetDatabase.GetAssetPath(asset);
			if (string.IsNullOrEmpty(path)) {
				Debug.LogWarning("GpuSpine bake skipped: the SkeletonDataAsset is not persisted.", asset);
				return null;
			}
			string guid = AssetDatabase.AssetPathToGUID(path);
			string fingerprint = ComputeSourceFingerprint(asset, path);

			GpuSpineBakedData container = FindContainer(path);
			bool containerIsNew = container == null;
			if (containerIsNew) {
				container = ScriptableObject.CreateInstance<GpuSpineBakedData>();
				container.name = ContainerName;
			}

			SkeletonData data = asset.GetSkeletonData(true);
			GpuSpineAuditReport audit = GpuSpineAuditor.Audit(data);
			List<BakeTarget> targets = CollectTargets(data, audit, container);

			if (!containerIsNew && IsUpToDate(container, guid, fingerprint, targets))
				return container; // Nothing changed; no save, so the import loop terminates here.

			container.SkeletonDataAssetGuid = guid;
			container.BakedZSpacing = BakedZSpacing;
			container.SourceFingerprint = fingerprint;
			container.Audit = audit;
			ClearEntries(container);

			List<GpuSpineBakedEntry> entries = new List<GpuSpineBakedEntry>(targets.Count);
			HashSet<string> seenKeys = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < targets.Count; i++) {
				BakeTarget target = targets[i];
				if (!seenKeys.Add(target.Key)) continue; // Different name, same content: first target wins.
				int seededFailures = audit.Failures.Count;
				int seededWarnings = audit.Warnings.Count;
				// Per-entry report seeded from the audit, so baker findings merge back deduplicated
				// instead of accumulating per combination.
				GpuSpineAuditReport entryReport = SeedEntryReport(audit);
				GpuSpineBakedEntry entry = GpuSpineBaker.Bake(data, target.SkinNames, BakedZSpacing, entryReport);
				entry.DisplayName = target.DisplayName;
				MergeEntryReport(audit, entryReport, seededFailures, seededWarnings);
				entries.Add(entry);
			}
			container.Entries = entries;

			// Persist: container first (it must be persistent before meshes can hang off it), then meshes.
			if (containerIsNew) AssetDatabase.AddObjectToAsset(container, asset);
			for (int i = 0; i < entries.Count; i++) {
				Mesh mesh = entries[i].Mesh;
				if (mesh != null) AssetDatabase.AddObjectToAsset(mesh, container);
			}
			EditorUtility.SetDirty(container);
			EditorUtility.SetDirty(asset);
			AssetDatabase.SaveAssets();
			Debug.Log("GpuSpine baked '" + path + "': " + entries.Count + " entries, audit "
				+ (audit.Passed ? "passed" : "FAILED (" + audit.Failures.Count + " failure(s))")
				+ (audit.DynamicSlots.Count > 0 ? ", " + audit.DynamicSlots.Count + " dynamic slot(s)" : "")
				+ (audit.Warnings.Count > 0 ? ", " + audit.Warnings.Count + " warning(s)" : "") + ".", asset);
			return container;
		}

		/// <summary>
		/// Builds the bake target set: the default entry, one entry per skin in SkeletonData.Skins and
		/// one entry per valid declared combination of the container. Empty when the audit failed (the
		/// container then keeps the report only). Combinations referencing unknown skin names are
		/// skipped with a warning.
		/// </summary>
		static List<BakeTarget> CollectTargets (SkeletonData data, GpuSpineAuditReport audit, GpuSpineBakedData container) {
			List<BakeTarget> targets = new List<BakeTarget>();
			if (data == null || !audit.Passed) return targets;
			targets.Add(new BakeTarget { DisplayName = "default", SkinNames = new string[0], Key = ComputeKey(data, null) });
			Skin[] skins = data.Skins.Items;
			for (int i = 0, n = data.Skins.Count; i < n; i++) {
				Skin skin = skins[i];
				if (skin == null) continue;
				string[] skinNames = { skin.Name };
				targets.Add(new BakeTarget { DisplayName = skin.Name, SkinNames = skinNames, Key = ComputeKey(data, skinNames) });
			}
			List<GpuSpineComboDeclaration> combos = container.DeclaredCombos;
			if (combos != null) {
				for (int i = 0; i < combos.Count; i++) {
					GpuSpineComboDeclaration combo = combos[i];
					if (combo == null || combo.SkinNames == null || combo.SkinNames.Length == 0) continue;
					if (!SkinsExist(data, combo.SkinNames)) {
						Debug.LogWarning("GpuSpine bake skips declared combination '" + combo.DisplayName
							+ "': a skin name does not exist in the SkeletonData.", container);
						continue;
					}
					string[] skinNames = (string[])combo.SkinNames.Clone();
					targets.Add(new BakeTarget {
						DisplayName = string.IsNullOrEmpty(combo.DisplayName) ? string.Join("+", skinNames) : combo.DisplayName,
						SkinNames = skinNames,
						Key = ComputeKey(data, skinNames)
					});
				}
			}
			return targets;
		}

		/// <summary>Precomputes a target's content key with the same effective-skin construction the
		/// baker uses, so duplicate-content targets are skipped before baking.</summary>
		static string ComputeKey (SkeletonData data, string[] skinNames) {
			Skin effectiveSkin = GpuSpineBaker.BuildEffectiveSkin(data, skinNames, out _);
			return GpuSpineBakeKey.Compute(data, effectiveSkin);
		}

		static bool SkinsExist (SkeletonData data, string[] skinNames) {
			for (int i = 0; i < skinNames.Length; i++)
				if (data.FindSkin(skinNames[i]) == null) return false;
			return true;
		}

		/// <summary>
		/// No-change check, compared against the already persisted container: owning asset GUID, source
		/// fingerprint and the set of unique target keys must all match. Entries with a null mesh record
		/// a bake-time hard failure and count as up to date; use the manual Rebake menu to repair
		/// manually deleted meshes.
		/// </summary>
		static bool IsUpToDate (GpuSpineBakedData container, string guid, string fingerprint, List<BakeTarget> targets) {
			if (container.SkeletonDataAssetGuid != guid) return false;
			if (container.SourceFingerprint != fingerprint) return false;
			HashSet<string> uniqueKeys = new HashSet<string>(StringComparer.Ordinal);
			for (int i = 0; i < targets.Count; i++) uniqueKeys.Add(targets[i].Key);
			List<GpuSpineBakedEntry> entries = container.Entries;
			if (entries == null || entries.Count != uniqueKeys.Count) return false;
			foreach (string key in uniqueKeys)
				if (container.FindEntry(key) == null) return false;
			return true;
		}

		/// <summary>Removes the old entry meshes from the asset and destroys them (prevents orphaned
		/// sub-asset leaks), then resets the entry list.</summary>
		static void ClearEntries (GpuSpineBakedData container) {
			List<GpuSpineBakedEntry> entries = container.Entries;
			if (entries != null) {
				for (int i = 0; i < entries.Count; i++) {
					Mesh mesh = entries[i] != null ? entries[i].Mesh : null;
					if (mesh == null) continue;
					AssetDatabase.RemoveObjectFromAsset(mesh);
					UnityEngine.Object.DestroyImmediate(mesh);
				}
			}
			container.Entries = new List<GpuSpineBakedEntry>();
		}

		/// <summary>Seeds a per-entry bake report from the shared audit (the dynamic slot table is
		/// shared read-only; the baker never mutates it).</summary>
		static GpuSpineAuditReport SeedEntryReport (GpuSpineAuditReport audit) {
			GpuSpineAuditReport report = new GpuSpineAuditReport {
				Passed = audit.Passed,
				HasDrawOrderTimeline = audit.HasDrawOrderTimeline,
				HasClipping = audit.HasClipping,
				DynamicSlots = audit.DynamicSlots
			};
			report.Failures.AddRange(audit.Failures);
			report.Warnings.AddRange(audit.Warnings);
			return report;
		}

		/// <summary>Merges a per-entry bake report back into the container audit: deduplicated new
		/// failures and warnings (anything beyond the seeded prefix), the hard-failure flag, and the
		/// truncation count as a maximum (the same attachment truncates identically in every
		/// combination containing it).</summary>
		static void MergeEntryReport (GpuSpineAuditReport audit, GpuSpineAuditReport entryReport, int seededFailures, int seededWarnings) {
			for (int i = seededFailures; i < entryReport.Failures.Count; i++) {
				string failure = entryReport.Failures[i];
				if (!audit.Failures.Contains(failure)) audit.Failures.Add(failure);
			}
			for (int i = seededWarnings; i < entryReport.Warnings.Count; i++) {
				string warning = entryReport.Warnings[i];
				if (!audit.Warnings.Contains(warning)) audit.Warnings.Add(warning);
			}
			if (!entryReport.Passed) audit.Passed = false;
			if (entryReport.TruncatedVertexCount > audit.TruncatedVertexCount)
				audit.TruncatedVertexCount = entryReport.TruncatedVertexCount;
		}

		static GpuSpineBakedData FindContainer (string path) {
			UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
			for (int i = 0; i < subAssets.Length; i++) {
				GpuSpineBakedData container = subAssets[i] as GpuSpineBakedData;
				if (container != null) return container;
			}
			return null;
		}

		/// <summary>
		/// Fingerprint of the inputs that change the baked output: the dependency source files
		/// (skeleton, atlas, textures — path, length and last write time) plus the SkeletonDataAsset
		/// scale. The SkeletonDataAsset's own .asset file is excluded on purpose: the bake itself saves
		/// it, and including it would defeat the no-change check and loop the import. A content change
		/// that preserves both file length and write time is not detected (accepted, documented); the
		/// manual Rebake menu covers it.
		/// </summary>
		static string ComputeSourceFingerprint (SkeletonDataAsset asset, string path) {
			string projectRoot = Directory.GetParent(Application.dataPath).FullName;
			string[] dependencies = AssetDatabase.GetDependencies(path, true);
			Array.Sort(dependencies, StringComparer.Ordinal);
			StringBuilder builder = new StringBuilder(dependencies.Length * 64);
			builder.Append("v1|scale:").Append(asset.scale.ToString("R", CultureInfo.InvariantCulture));
			for (int i = 0; i < dependencies.Length; i++) {
				string dependency = dependencies[i];
				if (dependency == path) continue;
				builder.Append('|').Append(dependency).Append('#');
				FileInfo file = new FileInfo(Path.Combine(projectRoot, dependency));
				if (file.Exists)
					builder.Append(file.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
						.Append(file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
				else
					builder.Append("missing");
			}
			return builder.ToString();
		}
	}
}
