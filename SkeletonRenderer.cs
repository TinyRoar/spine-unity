/******************************************************************************
 * Spine Runtimes Software License
 * Version 2.3
 * 
 * Copyright (c) 2013-2015, Esoteric Software
 * All rights reserved.
 * 
 * You are granted a perpetual, non-exclusive, non-sublicensable and
 * non-transferable license to use, install, execute and perform the Spine
 * Runtimes Software (the "Software") and derivative works solely for personal
 * or internal use. Without the written permission of Esoteric Software (see
 * Section 2 of the Spine Software License Agreement), you may not (a) modify,
 * translate, adapt or otherwise create derivative works, improvements of the
 * Software or develop new applications using the Software or (b) remove,
 * delete, alter or obscure any trademarks or any copyright, trademark, patent
 * or other intellectual property or proprietary rights notices on or in the
 * Software, including any copy thereof. Redistributions in binary or source
 * form must include this license and terms.
 * 
 * THIS SOFTWARE IS PROVIDED BY ESOTERIC SOFTWARE "AS IS" AND ANY EXPRESS OR
 * IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO
 * EVENT SHALL ESOTERIC SOFTWARE BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
 * PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS;
 * OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR
 * OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *****************************************************************************/
#define SPINE_OPTIONAL_NORMALS
#define SPINE_OPTIONAL_FRONTFACING

using System;
using System.Collections.Generic;
using UnityEngine;
using Spine;
using Spine.Unity;
using Spine.Unity.MeshGeneration;

/// <summary>Renders a skeleton.</summary>
[ExecuteInEditMode, RequireComponent(typeof(MeshFilter), typeof(MeshRenderer)), DisallowMultipleComponent]
public class SkeletonRenderer : MonoBehaviour {

	public delegate void SkeletonRendererDelegate (SkeletonRenderer skeletonRenderer);
	public SkeletonRendererDelegate OnRebuild;

	public SkeletonDataAsset skeletonDataAsset;
	public String initialSkinName;

	#region Advanced
	#if SPINE_OPTIONAL_NORMALS
	public bool calculateNormals, calculateTangents;
	#endif
	public float zSpacing;
	public bool renderMeshes = true, immutableTriangles;
	#if SPINE_OPTIONAL_FRONTFACING
	public bool frontFacing;
	#endif
	public bool logErrors = false;

	// Submesh Separation
	[SpineSlot] public string[] submeshSeparators = new string[0];
	[System.NonSerialized] public List<Slot> submeshSeparatorSlots = new List<Slot>();

	// Custom Slot Material
	[System.NonSerialized] readonly Dictionary<Slot, Material> customSlotMaterials = new Dictionary<Slot, Material>();
	public Dictionary<Slot, Material> CustomSlotMaterials { get { return customSlotMaterials; } }

	// Custom Mesh Generation Override
	public delegate void InstructionDelegate (SkeletonRenderer.SmartMesh.Instruction instruction);
	public event InstructionDelegate GenerateMeshOverride;
	#endregion

	[System.NonSerialized] public bool valid;
	[System.NonSerialized] public Skeleton skeleton;

	MeshRenderer meshRenderer;
	MeshFilter meshFilter;

	Spine.Unity.DoubleBuffered<SkeletonRenderer.SmartMesh> doubleBufferedMesh;
	readonly SmartMesh.Instruction currentInstructions = new SmartMesh.Instruction();
	readonly ExposedList<SubmeshTriangleBuffer> submeshes = new ExposedList<SubmeshTriangleBuffer>();

	float[] tempVertices = new float[8];
	Vector3[] vertices;
	Color32[] colors;
	Vector2[] uvs;
	
	readonly ExposedList<Material> submeshMaterials = new ExposedList<Material>();
	Material[] sharedMaterials = new Material[0];

	#if SPINE_OPTIONAL_NORMALS
	Vector3[] normals;
	Vector4[] tangents;
	#endif

	#region Runtime Instantiation
	public static T NewSpineGameObject<T> (SkeletonDataAsset skeletonDataAsset) where T : SkeletonRenderer {
		return SkeletonRenderer.AddSpineComponent<T>(new GameObject("New Spine GameObject"), skeletonDataAsset);
	}

	/// <summary>Add and prepare a Spine component that derives from SkeletonRenderer to a GameObject at runtime.</summary>
	/// <typeparam name="T">T should be SkeletonRenderer or any of its derived classes.</typeparam>
	public static T AddSpineComponent<T> (GameObject gameObject, SkeletonDataAsset skeletonDataAsset) where T : SkeletonRenderer {
		var c = gameObject.AddComponent<T>();
		if (skeletonDataAsset != null) {
			c.skeletonDataAsset = skeletonDataAsset;
			c.Initialize(false);
		}
		return c;
	}
	#endregion

	public virtual void Awake () {
		Initialize(false);
	}

	public virtual void Initialize (bool overwrite) {
		if (valid && !overwrite)
			return;

		// Clear
		{
			if (meshFilter != null)
				meshFilter.sharedMesh = null;

			meshRenderer = GetComponent<MeshRenderer>();
			if (meshRenderer != null) meshRenderer.sharedMaterial = null;

			currentInstructions.Clear();
			vertices = null;
			colors = null;
			uvs = null;
			sharedMaterials = new Material[0];
			submeshMaterials.Clear();
			submeshes.Clear();
			skeleton = null;

			valid = false;
		}

		if (!skeletonDataAsset) {
			if (logErrors)
				Debug.LogError("Missing SkeletonData asset.", this);

			return;
		}
		SkeletonData skeletonData = skeletonDataAsset.GetSkeletonData(false);
		if (skeletonData == null)
			return;
		valid = true;

		meshFilter = GetComponent<MeshFilter>();
		meshRenderer = GetComponent<MeshRenderer>();
		doubleBufferedMesh = new DoubleBuffered<SmartMesh>();
		vertices = new Vector3[0];

		skeleton = new Skeleton(skeletonData);
		if (initialSkinName != null && initialSkinName.Length > 0 && initialSkinName != "default")
			skeleton.SetSkin(initialSkinName);

		submeshSeparatorSlots.Clear();
		for (int i = 0; i < submeshSeparators.Length; i++) {
			submeshSeparatorSlots.Add(skeleton.FindSlot(submeshSeparators[i]));
		}

		LateUpdate();

		if (OnRebuild != null)
			OnRebuild(this);
	}

	public virtual void LateUpdate () {
		if (!valid)
			return;

		if (!meshRenderer.enabled && GenerateMeshOverride == null)
			return;

		// STEP 1. Determine a SmartMesh.Instruction. Split up instructions into submeshes.

		// This method caches several .Items arrays.
		// Never mutate their overlying ExposedList objects.
		ExposedList<Slot> drawOrder = skeleton.drawOrder;
		var drawOrderItems = drawOrder.Items;
		int drawOrderCount = drawOrder.Count;
		int submeshSeparatorSlotsCount = submeshSeparatorSlots.Count;
		bool renderMeshes = this.renderMeshes;

		// Clear last state of attachments and submeshes
		var workingInstruction = this.currentInstructions;
		var workingAttachments = workingInstruction.attachments;
		workingAttachments.Clear(false);
		workingAttachments.GrowIfNeeded(drawOrderCount);
		workingAttachments.Count = drawOrderCount;
		var workingAttachmentsItems = workingInstruction.attachments.Items;

		#if SPINE_OPTIONAL_FRONTFACING
		var workingFlips = workingInstruction.attachmentFlips;
		workingFlips.Clear(false);
		workingFlips.GrowIfNeeded(drawOrderCount);
		workingFlips.Count = drawOrderCount;
		var workingFlipsItems = workingFlips.Items;
		#endif

		var workingSubmeshInstructions = workingInstruction.submeshInstructions;	// Items array should not be cached. There is dynamic writing to this list.
		workingSubmeshInstructions.Clear(false);

		bool isCustomMaterialsPopulated = customSlotMaterials.Count > 0;

		int vertexCount = 0;
		int submeshVertexCount = 0;
		int submeshTriangleCount = 0, submeshFirstVertex = 0, submeshStartSlotIndex = 0;
		Material lastMaterial = null;
		for (int i = 0; i < drawOrderCount; i++) {
			Slot slot = drawOrderItems[i];
			Attachment attachment = slot.attachment;

			workingAttachmentsItems[i] = attachment;

			#if SPINE_OPTIONAL_FRONTFACING
			bool flip = frontFacing && (slot.bone.WorldSignX != slot.bone.WorldSignY);
			workingFlipsItems[i] = flip;
			#endif

			object rendererObject; // An AtlasRegion in plain Spine-Unity. Spine-TK2D hooks into TK2D's system. eventual source of Material object.
			int attachmentVertexCount, attachmentTriangleCount;

			var regionAttachment = attachment as RegionAttachment;
			if (regionAttachment != null) {
				rendererObject = regionAttachment.RendererObject;
				attachmentVertexCount = 4;
				attachmentTriangleCount = 6;
			} else {
				if (!renderMeshes)
					continue;
				var meshAttachment = attachment as MeshAttachment;
				if (meshAttachment != null) {
					rendererObject = meshAttachment.RendererObject;
					attachmentVertexCount = meshAttachment.vertices.Length >> 1;
					attachmentTriangleCount = meshAttachment.triangles.Length;
				} else {
					var skinnedMeshAttachment = attachment as WeightedMeshAttachment;
					if (skinnedMeshAttachment != null) {
						rendererObject = skinnedMeshAttachment.RendererObject;
						attachmentVertexCount = skinnedMeshAttachment.uvs.Length >> 1;
						attachmentTriangleCount = skinnedMeshAttachment.triangles.Length;
					} else
						continue;
				}
			}

			#if !SPINE_TK2D
			// Material material = (Material)((AtlasRegion)rendererObject).page.rendererObject; // For no customSlotMaterials

			Material material;
			if (isCustomMaterialsPopulated) {
				if (!customSlotMaterials.TryGetValue(slot, out material)) {
					material = (Material)((AtlasRegion)rendererObject).page.rendererObject;
				}
			} else {
				material = (Material)((AtlasRegion)rendererObject).page.rendererObject;
			}
			#else
			Material material = (rendererObject.GetType() == typeof(Material)) ? (Material)rendererObject : (Material)((AtlasRegion)rendererObject).page.rendererObject;
			#endif

			// Create a new SubmeshInstruction when material changes. (or when forced to separate by a submeshSeparator)
			bool separatedBySlot = (submeshSeparatorSlotsCount > 0 && submeshSeparatorSlots.Contains(slot));
			if ((vertexCount > 0 && lastMaterial.GetInstanceID() != material.GetInstanceID()) || separatedBySlot) {
				workingSubmeshInstructions.Add(
					new Spine.Unity.MeshGeneration.SubmeshInstruction {
						skeleton = this.skeleton,
						material = lastMaterial,
						startSlot = submeshStartSlotIndex,
						endSlot = i,
						triangleCount = submeshTriangleCount,
						firstVertexIndex = submeshFirstVertex,
						vertexCount = submeshVertexCount,
						separatedBySlot = separatedBySlot
					}
				);

				submeshTriangleCount = 0;
				submeshVertexCount = 0;
				submeshFirstVertex = vertexCount;
				submeshStartSlotIndex = i;
			}
			lastMaterial = material;

			submeshTriangleCount += attachmentTriangleCount;
			vertexCount += attachmentVertexCount;
			submeshVertexCount += attachmentVertexCount;
		}

		workingSubmeshInstructions.Add(
			new Spine.Unity.MeshGeneration.SubmeshInstruction {
				skeleton = this.skeleton,
				material = lastMaterial,
				startSlot = submeshStartSlotIndex,
				endSlot = drawOrderCount,
				triangleCount = submeshTriangleCount,
				firstVertexIndex = submeshFirstVertex,
				vertexCount = submeshVertexCount,
				separatedBySlot = false
			}
		);

		workingInstruction.vertexCount = vertexCount;
		workingInstruction.immutableTriangles = this.immutableTriangles;
		#if SPINE_OPTIONAL_FRONTFACING
		workingInstruction.frontFacing = this.frontFacing;
		#endif

		if (GenerateMeshOverride != null) {
			GenerateMeshOverride(workingInstruction);
			return;
		}

		// STEP 2. Update vertex buffer based on verts from the attachments.
		// Uses values that were also stored in workingInstruction.
		Vector3[] vertices = this.vertices;
		bool vertexCountIncreased = vertexCount > vertices.Length;	

		if (vertexCountIncreased) {
			this.vertices = vertices = new Vector3[vertexCount];
			this.colors = new Color32[vertexCount];
			this.uvs = new Vector2[vertexCount];

			#if SPINE_OPTIONAL_NORMALS
			if (calculateNormals) {
				Vector3[] localNormals = this.normals = new Vector3[vertexCount];
				Vector3 normal = new Vector3(0, 0, -1);
				for (int i = 0; i < vertexCount; i++)
					localNormals[i] = normal;

				if (calculateTangents) {
					Vector4[] localTangents = this.tangents = new Vector4[vertexCount];
					Vector4 tangent = new Vector4(1, 0, 0, -1);
					for (int i = 0; i < vertexCount; i++)
						localTangents[i] = tangent;
				}
			}
			#endif
		} else {
			Vector3 zero = Vector3.zero;
			for (int i = vertexCount, n = vertices.Length; i < n; i++)
				vertices[i] = zero;
		}

		float zSpacing = this.zSpacing;
		float[] tempVertices = this.tempVertices;
		Vector2[] uvs = this.uvs;
		Color32[] colors = this.colors;
		int vertexIndex = 0;
		Color32 color;
		float a = skeleton.a * 255, r = skeleton.r, g = skeleton.g, b = skeleton.b;

		Vector3 meshBoundsMin;
		Vector3 meshBoundsMax;
		if (vertexCount == 0) {
			meshBoundsMin = new Vector3(0, 0, 0);
			meshBoundsMax = new Vector3(0, 0, 0);
		} else {
			meshBoundsMin.x = int.MaxValue;
			meshBoundsMin.y = int.MaxValue;
			meshBoundsMax.x = int.MinValue;
			meshBoundsMax.y = int.MinValue;
			if (zSpacing > 0f) {
				meshBoundsMin.z = 0f;
				meshBoundsMax.z = zSpacing * (drawOrderCount - 1);
			} else {
				meshBoundsMin.z = zSpacing * (drawOrderCount - 1);
				meshBoundsMax.z = 0f;
			}
			int i = 0;
			do {
				Slot slot = drawOrderItems[i];
				Attachment attachment = slot.attachment;
				RegionAttachment regionAttachment = attachment as RegionAttachment;
				if (regionAttachment != null) {
					regionAttachment.ComputeWorldVertices(slot.bone, tempVertices);

					float z = i * zSpacing;
					float x1 = tempVertices[RegionAttachment.X1], y1 = tempVertices[RegionAttachment.Y1];
					float x2 = tempVertices[RegionAttachment.X2], y2 = tempVertices[RegionAttachment.Y2];
					float x3 = tempVertices[RegionAttachment.X3], y3 = tempVertices[RegionAttachment.Y3];
					float x4 = tempVertices[RegionAttachment.X4], y4 = tempVertices[RegionAttachment.Y4];
					vertices[vertexIndex].x = x1;
					vertices[vertexIndex].y = y1;
					vertices[vertexIndex].z = z;
					vertices[vertexIndex + 1].x = x4;
					vertices[vertexIndex + 1].y = y4;
					vertices[vertexIndex + 1].z = z;
					vertices[vertexIndex + 2].x = x2;
					vertices[vertexIndex + 2].y = y2;
					vertices[vertexIndex + 2].z = z;
					vertices[vertexIndex + 3].x = x3;
					vertices[vertexIndex + 3].y = y3;
					vertices[vertexIndex + 3].z = z;

					color.a = (byte)(a * slot.a * regionAttachment.a);
					color.r = (byte)(r * slot.r * regionAttachment.r * color.a);
					color.g = (byte)(g * slot.g * regionAttachment.g * color.a);
					color.b = (byte)(b * slot.b * regionAttachment.b * color.a);
					if (slot.data.blendMode == BlendMode.additive) color.a = 0;
					colors[vertexIndex] = color;
					colors[vertexIndex + 1] = color;
					colors[vertexIndex + 2] = color;
					colors[vertexIndex + 3] = color;

					float[] regionUVs = regionAttachment.uvs;
					uvs[vertexIndex].x = regionUVs[RegionAttachment.X1];
					uvs[vertexIndex].y = regionUVs[RegionAttachment.Y1];
					uvs[vertexIndex + 1].x = regionUVs[RegionAttachment.X4];
					uvs[vertexIndex + 1].y = regionUVs[RegionAttachment.Y4];
					uvs[vertexIndex + 2].x = regionUVs[RegionAttachment.X2];
					uvs[vertexIndex + 2].y = regionUVs[RegionAttachment.Y2];
					uvs[vertexIndex + 3].x = regionUVs[RegionAttachment.X3];
					uvs[vertexIndex + 3].y = regionUVs[RegionAttachment.Y3];

					// Calculate min/max X
					if (x1 < meshBoundsMin.x)
						meshBoundsMin.x = x1;
					else if (x1 > meshBoundsMax.x)
						meshBoundsMax.x = x1;
					if (x2 < meshBoundsMin.x)
						meshBoundsMin.x = x2;
					else if (x2 > meshBoundsMax.x)
						meshBoundsMax.x = x2;
					if (x3 < meshBoundsMin.x)
						meshBoundsMin.x = x3;
					else if (x3 > meshBoundsMax.x)
						meshBoundsMax.x = x3;
					if (x4 < meshBoundsMin.x)
						meshBoundsMin.x = x4;
					else if (x4 > meshBoundsMax.x)
						meshBoundsMax.x = x4;

					// Calculate min/max Y
					if (y1 < meshBoundsMin.y)
						meshBoundsMin.y = y1;
					else if (y1 > meshBoundsMax.y)
						meshBoundsMax.y = y1;
					if (y2 < meshBoundsMin.y)
						meshBoundsMin.y = y2;
					else if (y2 > meshBoundsMax.y)
						meshBoundsMax.y = y2;
					if (y3 < meshBoundsMin.y)
						meshBoundsMin.y = y3;
					else if (y3 > meshBoundsMax.y)
						meshBoundsMax.y = y3;
					if (y4 < meshBoundsMin.y)
						meshBoundsMin.y = y4;
					else if (y4 > meshBoundsMax.y)
						meshBoundsMax.y = y4;

					vertexIndex += 4;
				} else {
					if (!renderMeshes)
						continue;
					MeshAttachment meshAttachment = attachment as MeshAttachment;
					if (meshAttachment != null) {
						int meshVertexCount = meshAttachment.vertices.Length;
						if (tempVertices.Length < meshVertexCount)
							this.tempVertices = tempVertices = new float[meshVertexCount];
						meshAttachment.ComputeWorldVertices(slot, tempVertices);

						color.a = (byte)(a * slot.a * meshAttachment.a);
						color.r = (byte)(r * slot.r * meshAttachment.r * color.a);
						color.g = (byte)(g * slot.g * meshAttachment.g * color.a);
						color.b = (byte)(b * slot.b * meshAttachment.b * color.a);
						if (slot.data.blendMode == BlendMode.additive) color.a = 0;

						float[] meshUVs = meshAttachment.uvs;
						float z = i * zSpacing;
						for (int ii = 0; ii < meshVertexCount; ii += 2, vertexIndex++) {
							float x = tempVertices[ii], y = tempVertices[ii + 1];
							vertices[vertexIndex].x = x;
							vertices[vertexIndex].y = y;
							vertices[vertexIndex].z = z;
							colors[vertexIndex] = color;
							uvs[vertexIndex].x = meshUVs[ii];
							uvs[vertexIndex].y = meshUVs[ii + 1];

							if (x < meshBoundsMin.x)
								meshBoundsMin.x = x;
							else if (x > meshBoundsMax.x)
								meshBoundsMax.x = x;
							if (y < meshBoundsMin.y)
								meshBoundsMin.y = y;
							else if (y > meshBoundsMax.y)
								meshBoundsMax.y = y;
						}
					} else {
						WeightedMeshAttachment weightedMeshAttachment = attachment as WeightedMeshAttachment;
						if (weightedMeshAttachment != null) {
							int meshVertexCount = weightedMeshAttachment.uvs.Length;
							if (tempVertices.Length < meshVertexCount)
								this.tempVertices = tempVertices = new float[meshVertexCount];
							weightedMeshAttachment.ComputeWorldVertices(slot, tempVertices);

							color.a = (byte)(a * slot.a * weightedMeshAttachment.a);
							color.r = (byte)(r * slot.r * weightedMeshAttachment.r * color.a);
							color.g = (byte)(g * slot.g * weightedMeshAttachment.g * color.a);
							color.b = (byte)(b * slot.b * weightedMeshAttachment.b * color.a);
							if (slot.data.blendMode == BlendMode.additive) color.a = 0;

							float[] meshUVs = weightedMeshAttachment.uvs;
							float z = i * zSpacing;
							for (int ii = 0; ii < meshVertexCount; ii += 2, vertexIndex++) {
								float x = tempVertices[ii], y = tempVertices[ii + 1];
								vertices[vertexIndex].x = x;
								vertices[vertexIndex].y = y;
								vertices[vertexIndex].z = z;
								colors[vertexIndex] = color;
								uvs[vertexIndex].x = meshUVs[ii];
								uvs[vertexIndex].y = meshUVs[ii + 1];

								if (x < meshBoundsMin.x)
									meshBoundsMin.x = x;
								else if (x > meshBoundsMax.x)
									meshBoundsMax.x = x;
								if (y < meshBoundsMin.y)
									meshBoundsMin.y = y;
								else if (y > meshBoundsMax.y)
									meshBoundsMax.y = y;
							}
						}
					}
				}
			} while (++i < drawOrderCount);
		}

		// Step 3. Move the mesh data into a UnityEngine.Mesh
		var currentSmartMesh = doubleBufferedMesh.GetNext();	// Double-buffer for performance.
		var currentMesh = currentSmartMesh.mesh;

		currentMesh.vertices = vertices;
		currentMesh.colors32 = colors;
		currentMesh.uv = uvs;
		var currentSmartMeshInstructionUsed = currentSmartMesh.instructionUsed;
		#if SPINE_OPTIONAL_NORMALS
		if (currentSmartMeshInstructionUsed.vertexCount < vertexCount) {
			if (calculateNormals) {
				currentMesh.normals = normals;
				if (calculateTangents) {
					currentMesh.tangents = tangents;
				}
			}
		}
		#endif

		// Check if the triangles should also be updated.
		// This thorough structure check is cheaper than updating triangles every frame.
		bool mustUpdateMeshStructure = CheckIfMustUpdateMeshStructure(workingInstruction, currentSmartMeshInstructionUsed);
		if (mustUpdateMeshStructure) {
			var thisSubmeshMaterials = this.submeshMaterials;
			thisSubmeshMaterials.Clear(false);

			int submeshCount = workingSubmeshInstructions.Count;
			int oldSubmeshCount = submeshes.Count;

			submeshes.Capacity = submeshCount;
			for (int i = oldSubmeshCount; i < submeshCount; i++)
				submeshes.Items[i] = new SubmeshTriangleBuffer();

			var mutableTriangles = !workingInstruction.immutableTriangles;
			for (int i = 0, last = submeshCount - 1; i < submeshCount; i++) {
				var submeshInstruction = workingSubmeshInstructions.Items[i];
				if (mutableTriangles || i >= oldSubmeshCount)
					SetSubmesh(i, submeshInstruction,
						#if SPINE_OPTIONAL_FRONTFACING
						currentInstructions.attachmentFlips,
						#endif
						i == last);
				thisSubmeshMaterials.Add(submeshInstruction.material);
			}

			currentMesh.subMeshCount = submeshCount;

			for (int i = 0; i < submeshCount; ++i)
				currentMesh.SetTriangles(submeshes.Items[i].triangles, i);
		}

		Vector3 meshBoundsExtents = meshBoundsMax - meshBoundsMin;
		Vector3 meshBoundsCenter = meshBoundsMin + meshBoundsExtents * 0.5f;
		currentMesh.bounds = new Bounds(meshBoundsCenter, meshBoundsExtents);

		// CheckIfMustUpdateMaterialArray (last pushed materials vs currently parsed materials)
		// Needs to check against the Working Submesh Instructions Materials instead of the cached submeshMaterials.
		{
			var lastPushedMaterials = this.sharedMaterials;
			bool mustUpdateRendererMaterials = mustUpdateMeshStructure ||
				(lastPushedMaterials.Length != workingSubmeshInstructions.Count);

			if (!mustUpdateRendererMaterials) {
				var workingSubmeshInstructionsItems = workingSubmeshInstructions.Items;
				for (int i = 0, n = lastPushedMaterials.Length; i < n; i++) {
					if (lastPushedMaterials[i].GetInstanceID() != workingSubmeshInstructionsItems[i].material.GetInstanceID()) {   // Bounds check is implied above.
						mustUpdateRendererMaterials = true;
						break;
					}
				}
			}

			if (mustUpdateRendererMaterials) {
				if (submeshMaterials.Count == sharedMaterials.Length)
					submeshMaterials.CopyTo(sharedMaterials);
				else
					sharedMaterials = submeshMaterials.ToArray();

				meshRenderer.sharedMaterials = sharedMaterials;
			}
		}

		// Step 4. The UnityEngine.Mesh is ready. Set it as the MeshFilter's mesh. Store the instructions used for that mesh.
		meshFilter.sharedMesh = currentMesh;
		currentSmartMesh.instructionUsed.Set(workingInstruction);


		// Step 5. Miscellaneous
		// Add stuff here if you want
	}

	static bool CheckIfMustUpdateMeshStructure (SmartMesh.Instruction a, SmartMesh.Instruction b) {
		
		#if UNITY_EDITOR
		if (!Application.isPlaying)
			return true;
		#endif

		if (a.vertexCount != b.vertexCount)
			return true;

		if (a.immutableTriangles != b.immutableTriangles)
			return true;

		int attachmentCountB = b.attachments.Count;
		if (a.attachments.Count != attachmentCountB) // Bounds check for the looped storedAttachments count below.
			return true;

		var attachmentsA = a.attachments.Items;
		var attachmentsB = b.attachments.Items;		
		for (int i = 0; i < attachmentCountB; i++) {
			if (attachmentsA[i] != attachmentsB[i])
				return true;
		}

		#if SPINE_OPTIONAL_FRONTFACING
		if (a.frontFacing != b.frontFacing) { 	// if settings changed
			return true;
		} else if (a.frontFacing) { 			// if settings matched, only need to check one.
			var flipsA = a.attachmentFlips.Items;
			var flipsB = b.attachmentFlips.Items;
			for (int i = 0; i < attachmentCountB; i++) {
				if (flipsA[i] != flipsB[i])
					return true;
			}
		}
		#endif

		// Submesh count changed
		int submeshCountA = a.submeshInstructions.Count;
		int submeshCountB = b.submeshInstructions.Count;
		if (submeshCountA != submeshCountB)
			return true;

		// Submesh Instruction mismatch
		var submeshInstructionsItemsA = a.submeshInstructions.Items;
		var submeshInstructionsItemsB = b.submeshInstructions.Items;
		for (int i = 0; i < submeshCountB; i++) {
			var submeshA = submeshInstructionsItemsA[i];
			var submeshB = submeshInstructionsItemsB[i];

			if (!(
				submeshA.vertexCount == submeshB.vertexCount &&
				submeshA.startSlot == submeshB.startSlot &&
				submeshA.endSlot == submeshB.endSlot &&
				submeshA.triangleCount == submeshB.triangleCount &&
				submeshA.firstVertexIndex == submeshB.firstVertexIndex
			))
				return true;			
		}

		return false;
	}

	#if SPINE_OPTIONAL_FRONTFACING
	void SetSubmesh (int submeshIndex, Spine.Unity.MeshGeneration.SubmeshInstruction submeshInstructions, ExposedList<bool> flipStates, bool isLastSubmesh) {
	#else
	void SetSubmesh (int submeshIndex, Spine.Unity.MeshGeneration.SubmeshInstruction submeshInstructions, bool isLastSubmesh) {
	#endif
		SubmeshTriangleBuffer currentSubmesh = submeshes.Items[submeshIndex];
		int[] triangles = currentSubmesh.triangles;

		int triangleCount = submeshInstructions.triangleCount;
		int firstVertex = submeshInstructions.firstVertexIndex;

		int trianglesCapacity = triangles.Length;
		if (isLastSubmesh && trianglesCapacity > triangleCount) {
			// Last submesh may have more triangles than required, so zero triangles to the end.
			for (int i = triangleCount; i < trianglesCapacity; i++)
				triangles[i] = 0;
			
			currentSubmesh.triangleCount = triangleCount;

		} else if (trianglesCapacity != triangleCount) {
			// Reallocate triangles when not the exact size needed.
			currentSubmesh.triangles = triangles = new int[triangleCount];
			currentSubmesh.triangleCount = 0;
		}

		#if SPINE_OPTIONAL_FRONTFACING
		if (!this.renderMeshes && !this.frontFacing) {
		#else
		if (!this.renderMeshes) {
		#endif
			// Use stored triangles if possible.
			if (currentSubmesh.firstVertex != firstVertex || currentSubmesh.triangleCount < triangleCount) { //|| currentSubmesh.triangleCount == 0
				currentSubmesh.triangleCount = triangleCount;
				currentSubmesh.firstVertex = firstVertex;

				for (int i = 0; i < triangleCount; i += 6, firstVertex += 4) {
					triangles[i] = firstVertex;
					triangles[i + 1] = firstVertex + 2;
					triangles[i + 2] = firstVertex + 1;
					triangles[i + 3] = firstVertex + 2;
					triangles[i + 4] = firstVertex + 3;
					triangles[i + 5] = firstVertex + 1;
				}
			}
			return;
		}

		// This method caches several .Items arrays.
		// Never mutate their overlying ExposedList objects.

		#if SPINE_OPTIONAL_FRONTFACING
		var flipStatesItems = flipStates.Items;
		#endif

		// Iterate through all slots and store their triangles. 
		var drawOrderItems = skeleton.DrawOrder.Items;
		int triangleIndex = 0; // Modified by loop
		for (int i = submeshInstructions.startSlot, n = submeshInstructions.endSlot; i < n; i++) {			
			Attachment attachment = drawOrderItems[i].attachment;
			#if SPINE_OPTIONAL_FRONTFACING
			bool flip = frontFacing && flipStatesItems[i];

			// Add RegionAttachment triangles
			if (attachment is RegionAttachment) {
				if (!flip) {
					triangles[triangleIndex] = firstVertex;
					triangles[triangleIndex + 1] = firstVertex + 2;
					triangles[triangleIndex + 2] = firstVertex + 1;
					triangles[triangleIndex + 3] = firstVertex + 2;
					triangles[triangleIndex + 4] = firstVertex + 3;
					triangles[triangleIndex + 5] = firstVertex + 1;
				} else {
					triangles[triangleIndex] = firstVertex + 1;
					triangles[triangleIndex + 1] = firstVertex + 2;
					triangles[triangleIndex + 2] = firstVertex;
					triangles[triangleIndex + 3] = firstVertex + 1;
					triangles[triangleIndex + 4] = firstVertex + 3;
					triangles[triangleIndex + 5] = firstVertex + 2;
				}

				triangleIndex += 6;
				firstVertex += 4;
				continue;
			}
			#else
			if (attachment is RegionAttachment) {
				triangles[triangleIndex] = firstVertex;
				triangles[triangleIndex + 1] = firstVertex + 2;
				triangles[triangleIndex + 2] = firstVertex + 1;
				triangles[triangleIndex + 3] = firstVertex + 2;
				triangles[triangleIndex + 4] = firstVertex + 3;
				triangles[triangleIndex + 5] = firstVertex + 1;

				triangleIndex += 6;
				firstVertex += 4;
				continue;
			}
			#endif

			// Add (Weighted)MeshAttachment triangles
			int[] attachmentTriangles;
			int attachmentVertexCount;
			var meshAttachment = attachment as MeshAttachment;
			if (meshAttachment != null) {
				attachmentVertexCount = meshAttachment.vertices.Length >> 1; // length/2
				attachmentTriangles = meshAttachment.triangles;
			} else {
				var weightedMeshAttachment = attachment as WeightedMeshAttachment;
				if (weightedMeshAttachment != null) {
					attachmentVertexCount = weightedMeshAttachment.uvs.Length >> 1; // length/2
					attachmentTriangles = weightedMeshAttachment.triangles;
				} else
					continue;
			}

			#if SPINE_OPTIONAL_FRONTFACING
			if (flip) {
				for (int ii = 0, nn = attachmentTriangles.Length; ii < nn; ii += 3, triangleIndex += 3) {
					triangles[triangleIndex + 2] = firstVertex + attachmentTriangles[ii];
					triangles[triangleIndex + 1] = firstVertex + attachmentTriangles[ii + 1];
					triangles[triangleIndex] = firstVertex + attachmentTriangles[ii + 2];
				}
			} else {
				for (int ii = 0, nn = attachmentTriangles.Length; ii < nn; ii++, triangleIndex++) {
					triangles[triangleIndex] = firstVertex + attachmentTriangles[ii];
				}
			}
			#else
			for (int ii = 0, nn = attachmentTriangles.Length; ii < nn; ii++, triangleIndex++) {
				triangles[triangleIndex] = firstVertex + attachmentTriangles[ii];
			}
			#endif

			firstVertex += attachmentVertexCount;
		}
	}
		
	#if UNITY_EDITOR
	void OnDrawGizmos () {
		// Make scene view selection easier by drawing a clear gizmo over the skeleton.
		meshFilter = GetComponent<MeshFilter>();
		if (meshFilter == null) return;

		Mesh mesh = meshFilter.sharedMesh;
		if (mesh == null) return;

		Bounds meshBounds = mesh.bounds;
		Gizmos.color = Color.clear;
		Gizmos.matrix = transform.localToWorldMatrix;
		Gizmos.DrawCube(meshBounds.center, meshBounds.size);
	}
	#endif

	///<summary>This is a Mesh that also stores the instructions SkeletonRenderer generated for it.</summary>
	public class SmartMesh {
		public Mesh mesh = Spine.Unity.SpineMesh.NewMesh();
		public SmartMesh.Instruction instructionUsed = new SmartMesh.Instruction();		

		public class Instruction {
			public bool immutableTriangles;
			public int vertexCount = -1;
			public readonly ExposedList<Attachment> attachments = new ExposedList<Attachment>();
			public readonly ExposedList<Spine.Unity.MeshGeneration.SubmeshInstruction> submeshInstructions = new ExposedList<Spine.Unity.MeshGeneration.SubmeshInstruction>();

			#if SPINE_OPTIONAL_FRONTFACING
			public bool frontFacing;
			public readonly ExposedList<bool> attachmentFlips = new ExposedList<bool>();
			#endif

			public void Clear () {
				this.attachments.Clear(false);
				this.submeshInstructions.Clear(false);

				#if SPINE_OPTIONAL_FRONTFACING
				this.attachmentFlips.Clear(false);
				#endif
			}

			public void Set (Instruction other) {
				this.immutableTriangles = other.immutableTriangles;
				this.vertexCount = other.vertexCount;

				this.attachments.Clear(false);
				this.attachments.GrowIfNeeded(other.attachments.Capacity);
				this.attachments.Count = other.attachments.Count;
				other.attachments.CopyTo(this.attachments.Items);

				#if SPINE_OPTIONAL_FRONTFACING
				this.frontFacing = other.frontFacing;
				this.attachmentFlips.Clear(false);
				this.attachmentFlips.GrowIfNeeded(other.attachmentFlips.Capacity);
				this.attachmentFlips.Count = other.attachmentFlips.Count;
				if (this.frontFacing)
					other.attachmentFlips.CopyTo(this.attachmentFlips.Items);
				#endif

				this.submeshInstructions.Clear(false);
				this.submeshInstructions.GrowIfNeeded(other.submeshInstructions.Capacity);
				this.submeshInstructions.Count = other.submeshInstructions.Count;
				other.submeshInstructions.CopyTo(this.submeshInstructions.Items);
			}
		}
	}

	class SubmeshTriangleBuffer {
		public int[] triangles = new int[0];
		public int triangleCount;
		public int firstVertex = -1;
	}
}


