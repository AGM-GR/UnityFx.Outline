﻿// Copyright (C) 2019-2020 Alexander Bogarsukov. All rights reserved.
// See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityFx.Outline
{
	/// <summary>
	/// Helper class for outline rendering with <see cref="CommandBuffer"/>.
	/// </summary>
	/// <remarks>
	/// <para>The class can be used on its own or as part of a higher level systems. It is used
	/// by higher level outline implementations (<see cref="OutlineEffect"/> and
	/// <see cref="OutlineBehaviour"/>). It is fully compatible with Unity post processing stack as well.</para>
	/// <para>The class implements <see cref="IDisposable"/> to be used inside <see langword="using"/>
	/// block as shown in the code samples. Disposing <see cref="OutlineRenderer"/> does not dispose
	/// the corresponding <see cref="CommandBuffer"/>.</para>
	/// <para>Command buffer is not cleared before rendering. It is user responsibility to do so if needed.</para>
	/// </remarks>
	/// <example>
	/// var commandBuffer = new CommandBuffer();
	/// 
	/// using (var renderer = new OutlineRenderer(commandBuffer, BuiltinRenderTextureType.CameraTarget))
	/// {
	/// 	renderer.Render(renderers, resources, settings);
	/// }
	///
	/// camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, commandBuffer);
	/// </example>
	/// <example>
	/// [Preserve]
	/// public class OutlineEffectRenderer : PostProcessEffectRenderer<Outline>
	/// {
	/// 	public override void Init()
	/// 	{
	/// 		base.Init();
	///
	/// 		// Reuse fullscreen triangle mesh from PostProcessing (do not create own).
	/// 		settings.OutlineResources.FullscreenTriangleMesh = RuntimeUtilities.fullscreenTriangle;
	/// 	}
	///
	/// 	public override void Render(PostProcessRenderContext context)
	/// 	{
	/// 		var resources = settings.OutlineResources;
	/// 		var layers = settings.OutlineLayers;
	///
	/// 		if (resources && resources.IsValid && layers)
	/// 		{
	/// 			// No need to setup property sheet parameters, all the rendering staff is handled by the OutlineRenderer.
	/// 			using (var renderer = new OutlineRenderer(context.command, context.source, context.destination))
	/// 			{
	/// 				layers.Render(renderer, resources);
	/// 			}
	/// 		}
	/// 	}
	/// }
	/// </example>
	/// <seealso cref="OutlineResources"/>
	public readonly struct OutlineRenderer : IDisposable
	{
		#region data

		private const int _hPassId = 0;
		private const int _vPassId = 1;

		private static readonly int _maskRtId = Shader.PropertyToID("_MaskTex");
		private static readonly int _hPassRtId = Shader.PropertyToID("_HPassTex");

		private readonly RenderTargetIdentifier _rt;
		private readonly RenderTargetIdentifier _depth;
		private readonly CommandBuffer _commandBuffer;

		#endregion

		#region interface

		/// <summary>
		/// A default <see cref="CameraEvent"/> outline rendering should be assosiated with.
		/// </summary>
		public const CameraEvent RenderEvent = CameraEvent.BeforeImageEffects;

		/// <summary>
		/// Name of the outline effect.
		/// </summary>
		public const string EffectName = "Outline";

		/// <summary>
		/// Initializes a new instance of the <see cref="OutlineRenderer"/> struct.
		/// </summary>
		/// <param name="cmd">A <see cref="CommandBuffer"/> to render the effect to. It should be cleared manually (if needed) before passing to this method.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="cmd"/> is <see langword="null"/>.</exception>
		public OutlineRenderer(CommandBuffer cmd)
			: this(cmd, BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.Depth, default)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="OutlineRenderer"/> struct.
		/// </summary>
		/// <param name="cmd">A <see cref="CommandBuffer"/> to render the effect to. It should be cleared manually (if needed) before passing to this method.</param>
		/// <param name="renderingPath">The rendering path of target camera (<see cref="Camera.actualRenderingPath"/>).</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="cmd"/> is <see langword="null"/>.</exception>
		public OutlineRenderer(CommandBuffer cmd, RenderingPath renderingPath)
			: this(cmd, BuiltinRenderTextureType.CameraTarget, GetBuiltinDepth(renderingPath), default)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="OutlineRenderer"/> struct.
		/// </summary>
		/// <param name="cmd">A <see cref="CommandBuffer"/> to render the effect to. It should be cleared manually (if needed) before passing to this method.</param>
		/// <param name="dst">Render target.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="cmd"/> is <see langword="null"/>.</exception>
		public OutlineRenderer(CommandBuffer cmd, RenderTargetIdentifier dst)
			: this(cmd, dst, BuiltinRenderTextureType.Depth, default)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="OutlineRenderer"/> struct.
		/// </summary>
		/// <param name="cmd">A <see cref="CommandBuffer"/> to render the effect to. It should be cleared manually (if needed) before passing to this method.</param>
		/// <param name="dst">Render target.</param>
		/// <param name="depth">Depth dexture to use.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="cmd"/> is <see langword="null"/>.</exception>
		public OutlineRenderer(CommandBuffer cmd, RenderTargetIdentifier dst, RenderTargetIdentifier depth)
			: this(cmd, dst, depth, default)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="OutlineRenderer"/> struct.
		/// </summary>
		/// <param name="cmd">A <see cref="CommandBuffer"/> to render the effect to. It should be cleared manually (if needed) before passing to this method.</param>
		/// <param name="dst">Render target.</param>
		/// <param name="depth">Depth dexture to use.</param>
		/// <param name="rtDesc">TODO</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="cmd"/> is <see langword="null"/>.</exception>
		public OutlineRenderer(CommandBuffer cmd, RenderTargetIdentifier dst, RenderTargetIdentifier depth, RenderTextureDescriptor rtDesc)
		{
			if (cmd is null)
			{
				throw new ArgumentNullException(nameof(cmd));
			}

			if (rtDesc.width <= 0)
			{
				rtDesc.width = -1;
			}

			if (rtDesc.height <= 0)
			{
				rtDesc.height = -1;
			}

			if (rtDesc.dimension == TextureDimension.None || rtDesc.dimension == TextureDimension.Unknown)
			{
				// TODO: Remove this.
				cmd.BeginSample(EffectName);
				cmd.GetTemporaryRT(_maskRtId, rtDesc.width, rtDesc.height, 0, FilterMode.Bilinear, RenderTextureFormat.R8);
				cmd.GetTemporaryRT(_hPassRtId, rtDesc.width, rtDesc.height, 0, FilterMode.Bilinear, RenderTextureFormat.R8);
			}
			else
			{
				rtDesc.shadowSamplingMode = ShadowSamplingMode.None;
				rtDesc.depthBufferBits = 0;
				rtDesc.colorFormat = RenderTextureFormat.R8;

				cmd.BeginSample(EffectName);
				cmd.GetTemporaryRT(_maskRtId, rtDesc, FilterMode.Bilinear);
				cmd.GetTemporaryRT(_hPassRtId, rtDesc, FilterMode.Bilinear);
			}

			_rt = dst;
			_depth = depth;
			_commandBuffer = cmd;
		}

		/// <summary>
		/// Renders outline around a single object. This version allows enumeration of <paramref name="renderers"/> with no GC allocations.
		/// </summary>
		/// <param name="obj">An object to be outlines.</param>
		/// <param name="resources">Outline resources.</param>
		/// <param name="settings">Outline settings.</param>
		/// <param name="renderingPath">Rendering path used by the target camera (used is <see cref="OutlineRenderFlags.EnableDepthTesting"/> is set).</param>
		/// <exception cref="ArgumentNullException">Thrown if any of the arguments is <see langword="null"/>.</exception>
		/// <seealso cref="Render(IEnumerable{Renderer}, OutlineResources, IOutlineSettings)"/>
		/// <seealso cref="Render(Renderer, OutlineResources, IOutlineSettings)"/>
		public void Render(OutlineObject obj, OutlineResources resources)
		{
			Render(obj.Renderers, resources, obj.OutlineSettings);
		}

		/// <summary>
		/// Renders outline around a single object. This version allows enumeration of <paramref name="renderers"/> with no GC allocations.
		/// </summary>
		/// <param name="renderers">One or more renderers representing a single object to be outlined.</param>
		/// <param name="resources">Outline resources.</param>
		/// <param name="settings">Outline settings.</param>
		/// <param name="renderingPath">Rendering path used by the target camera (used is <see cref="OutlineRenderFlags.EnableDepthTesting"/> is set).</param>
		/// <exception cref="ArgumentNullException">Thrown if any of the arguments is <see langword="null"/>.</exception>
		/// <seealso cref="Render(IEnumerable{Renderer}, OutlineResources, IOutlineSettings)"/>
		/// <seealso cref="Render(Renderer, OutlineResources, IOutlineSettings)"/>
		public void Render(IReadOnlyList<Renderer> renderers, OutlineResources resources, IOutlineSettings settings)
		{
			if (renderers is null)
			{
				throw new ArgumentNullException(nameof(renderers));
			}

			if (resources is null)
			{
				throw new ArgumentNullException(nameof(resources));
			}

			if (settings is null)
			{
				throw new ArgumentNullException(nameof(settings));
			}

			if (renderers.Count > 0)
			{
				RenderObject(resources, settings, renderers);
				RenderOutline(resources, settings);
			}
		}

		/// <summary>
		/// Renders outline around a single object.
		/// </summary>
		/// <param name="renderer">A <see cref="Renderer"/> representing an object to be outlined.</param>
		/// <param name="resources">Outline resources.</param>
		/// <param name="settings">Outline settings.</param>
		/// <param name="renderingPath">Rendering path used by the target camera (used is <see cref="OutlineRenderFlags.EnableDepthTesting"/> is set).</param>
		/// <exception cref="ArgumentNullException">Thrown if any of the arguments is <see langword="null"/>.</exception>
		/// <seealso cref="Render(IList{Renderer}, OutlineResources, IOutlineSettings)"/>
		/// <seealso cref="Render(IEnumerable{Renderer}, OutlineResources, IOutlineSettings)"/>
		public void Render(Renderer renderer, OutlineResources resources, IOutlineSettings settings)
		{
			if (renderer is null)
			{
				throw new ArgumentNullException(nameof(renderer));
			}

			if (resources is null)
			{
				throw new ArgumentNullException(nameof(resources));
			}

			if (settings is null)
			{
				throw new ArgumentNullException(nameof(settings));
			}

			RenderObject(resources, settings, renderer);
			RenderOutline(resources, settings);
		}

		/// <summary>
		/// TODO
		/// </summary>
		public static RenderTargetIdentifier GetBuiltinDepth(RenderingPath renderingPath)
		{
			return (renderingPath == RenderingPath.DeferredShading || renderingPath == RenderingPath.DeferredLighting) ? BuiltinRenderTextureType.ResolvedDepth : BuiltinRenderTextureType.Depth;
		}

		#endregion

		#region IDisposable

		/// <summary>
		/// Finalizes the effect rendering and releases temporary textures used. Should only be called once.
		/// </summary>
		public void Dispose()
		{
			_commandBuffer.ReleaseTemporaryRT(_hPassRtId);
			_commandBuffer.ReleaseTemporaryRT(_maskRtId);
			_commandBuffer.EndSample(EffectName);
		}

		#endregion

		#region implementation

		private void RenderObjectClear(OutlineRenderFlags flags)
		{
			if ((flags & OutlineRenderFlags.EnableDepthTesting) != 0)
			{
				// NOTE: Use the camera depth buffer when rendering the mask. Shader only reads from the depth buffer (ZWrite Off).
				_commandBuffer.SetRenderTarget(_maskRtId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, _depth, RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare);
			}
			else
			{
				_commandBuffer.SetRenderTarget(_maskRtId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
			}

			_commandBuffer.ClearRenderTarget(false, true, Color.clear);
		}

		private void RenderObject(OutlineResources resources, IOutlineSettings settings, IReadOnlyList<Renderer> renderers)
		{
			RenderObjectClear(settings.OutlineRenderMode);

			for (var i = 0; i < renderers.Count; ++i)
			{
				var r = renderers[i];

				if (r && r.enabled && r.isVisible && r.gameObject.activeInHierarchy)
				{
					// NOTE: Accessing Renderer.sharedMaterials triggers GC.Alloc. That's why we use a temporary
					// list of materials, cached with the outline resources.
					r.GetSharedMaterials(resources.TmpMaterials);

					for (var j = 0; j < resources.TmpMaterials.Count; ++j)
					{
						_commandBuffer.DrawRenderer(r, resources.RenderMaterial, j);
					}
				}
			}
		}

		private void RenderObject(OutlineResources resources, IOutlineSettings settings, Renderer renderer)
		{
			RenderObjectClear(settings.OutlineRenderMode);

			if (renderer && renderer.enabled && renderer.isVisible && renderer.gameObject.activeInHierarchy)
			{
				// NOTE: Accessing Renderer.sharedMaterials triggers GC.Alloc. That's why we use a temporary
				// list of materials, cached with the outline resources.
				renderer.GetSharedMaterials(resources.TmpMaterials);

				for (var i = 0; i < resources.TmpMaterials.Count; ++i)
				{
					_commandBuffer.DrawRenderer(renderer, resources.RenderMaterial, i);
				}
			}
		}

		private void RenderOutline(OutlineResources resources, IOutlineSettings settings)
		{
			var mat = resources.OutlineMaterial;
			var props = resources.GetProperties(settings);

			_commandBuffer.SetGlobalFloatArray(resources.GaussSamplesId, resources.GetGaussSamples(settings.OutlineWidth));

			// HPass
			_commandBuffer.SetRenderTarget(_hPassRtId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
			Blit(_commandBuffer, _maskRtId, resources, _hPassId, mat, props);

			// VPassBlend
			_commandBuffer.SetRenderTarget(_rt, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
			Blit(_commandBuffer, _hPassRtId, resources, _vPassId, mat, props);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void Init(CommandBuffer cmdBuffer, Vector2Int rtSize)
		{
			var cx = rtSize.x > 0 ? rtSize.x : -1;
			var cy = rtSize.y > 0 ? rtSize.y : -1;

			cmdBuffer.BeginSample(EffectName);
			cmdBuffer.GetTemporaryRT(_maskRtId, cx, cy, 0, FilterMode.Bilinear, RenderTextureFormat.R8);
			cmdBuffer.GetTemporaryRT(_hPassRtId, cx, cy, 0, FilterMode.Bilinear, RenderTextureFormat.R8);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static void Blit(CommandBuffer cmdBuffer, RenderTargetIdentifier src, OutlineResources resources, int shaderPass, Material mat, MaterialPropertyBlock props)
		{
			// Set source texture as _MainTex to match Blit behavior.
			cmdBuffer.SetGlobalTexture(resources.MainTexId, src);

			if (SystemInfo.graphicsShaderLevel < 35)
			{
				cmdBuffer.DrawMesh(resources.FullscreenTriangleMesh, Matrix4x4.identity, mat, 0, shaderPass, props);
			}
			else
			{
				cmdBuffer.DrawProcedural(Matrix4x4.identity, mat, shaderPass, MeshTopology.Triangles, 3, 1, props);
			}
		}

		#endregion
	}
}
