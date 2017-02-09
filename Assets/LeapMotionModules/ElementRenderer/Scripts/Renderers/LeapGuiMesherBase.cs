﻿using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Assertions;
#if UNITY_EDITOR
using UnityEditor.Sprites;
#endif
using Leap.Unity.Query;

public abstract class LeapGuiMesherBase : LeapGuiRenderer {

  [SerializeField]
  protected Shader _shader;

  [SerializeField]
  private PackUtil.Settings _atlasSettings;

  //Generated data
  protected List<Mesh> _meshes = new List<Mesh>();
  protected Material _material;

  //Current state of builder
  protected LeapGuiElement _currElement;
  protected int _currIndex;
  protected Mesh _currMesh;

  //Current mesh data being written to
  protected List<Vector3> _verts = new List<Vector3>();
  protected List<Vector3> _normals = new List<Vector3>();
  protected List<int> _tris = new List<int>();
  protected List<Color> _colors = new List<Color>();
  protected List<Vector4>[] _uvs = new List<Vector4>[4] {
    new List<Vector4>(),
    new List<Vector4>(),
    new List<Vector4>(),
    new List<Vector4>()
  };

  //Internal cache of requirements 
  private bool _doesRequireColors;
  private bool _doesRequireNormals;
  private bool _doesRequireUv3;
  private List<UVChannelFlags> _requiredUvChannels = new List<UVChannelFlags>();

  //Feature lists
  protected List<LeapGuiMeshFeature> _meshFeatures = new List<LeapGuiMeshFeature>();
  protected List<LeapGuiTextureFeature> _textureFeatures = new List<LeapGuiTextureFeature>();
  protected List<LeapGuiSpriteFeature> _spriteFeatures = new List<LeapGuiSpriteFeature>();
  protected List<LeapGuiTintFeature> _tintFeatures = new List<LeapGuiTintFeature>();
  protected List<LeapGuiBlendShapeFeature> _blendShapeFeatures = new List<LeapGuiBlendShapeFeature>();

  //#### Textures ####
  private Dictionary<UVChannelFlags, Rect[]> _packedRects;

  public override void OnEnableRenderer() {
    loadAllSupportedFeatures();

    //Sprite textures cannot be accessed at edit time, so we need to make sure
    //to upload them to the material right as the renderer is enabled
    if (_spriteFeatures != null) {
      uploadSpriteTextures();
    }
  }

  public override void OnUpdateRendererEditor() {
    loadAllSupportedFeatures();
    prepareMeshes();
    prepareMaterial();

    if (_meshFeatures.Count != 0) {
      if (_doesRequireColors) {
        _material.EnableKeyword(LeapGuiMeshFeature.COLORS_FEATURE);
      }

      if (_doesRequireNormals) {
        _material.EnableKeyword(LeapGuiMeshFeature.NORMALS_FEATURE);
      }

      foreach (var channel in _requiredUvChannels) {
        _material.EnableKeyword(LeapGuiMeshFeature.GetUvFeature(channel));
      }
    }

    if (_textureFeatures.Count != 0) {
      packTextures();
    }

    if (_spriteFeatures.Count != 0) {
      extractSpriteRects();
      uploadSpriteTextures();
    }

    if (_tintFeatures.Count != 0) {
      _material.EnableKeyword(LeapGuiTintFeature.FEATURE_NAME);
    }

    if (_blendShapeFeatures.Count != 0) {
      _material.EnableKeyword(LeapGuiBlendShapeFeature.FEATURE_NAME);
    }

    buildAllElements();
  }

  #region PRE GENERATION
  protected virtual void loadAllSupportedFeatures() {
    gui.GetSupportedFeatures(_meshFeatures);
    gui.GetSupportedFeatures(_textureFeatures);
    gui.GetSupportedFeatures(_spriteFeatures);
    gui.GetSupportedFeatures(_tintFeatures);
    gui.GetSupportedFeatures(_blendShapeFeatures);

    _doesRequireColors = doesRequireMeshColors();
    _doesRequireNormals = doesRequireMeshNormals();
    _doesRequireUv3 = doesRequireUvChannel(UVChannelFlags.UV3);
    foreach (var channel in MeshUtil.allUvChannels) {
      if (doesRequireUvChannel(channel)) {
        _requiredUvChannels.Add(channel);
      }
    }
  }

  protected virtual void prepareMeshes() {
    foreach (var mesh in _meshes) {
      DestroyImmediate(mesh);
    }
    _meshes.Clear();
  }

  protected virtual void prepareMaterial() {
    if (_material != null) {
      DestroyImmediate(_material);
    }

    _material = new Material(_shader);
    _material.name = "Procedural Gui Material";
  }

  protected virtual void packTextures() {
    using (new ProfilerSample("Pack Textures")) {
      Texture2D[] packedTextures;
      PackUtil.DoPack(_textureFeatures, _atlasSettings,
                  out packedTextures,
                  out _packedRects);

      for (int i = 0; i < _textureFeatures.Count; i++) {
        _material.SetTexture(_textureFeatures[i].propertyName, packedTextures[i]);
      }
    }
  }

  protected virtual void extractSpriteRects() {
    using (new ProfilerSample("Extract Sprite Rects")) {
      if (_packedRects == null) {
        _packedRects = new Dictionary<UVChannelFlags, Rect[]>();
      }

      foreach (var spriteFeature in _spriteFeatures) {
        var rects = new Rect[gui.elements.Count];
        _packedRects[spriteFeature.channel] = rects;

        for (int i = 0; i < spriteFeature.data.Count; i++) {
          var dataObj = spriteFeature.data[i];
          var sprite = dataObj.sprite;
          if (sprite == null) continue;

          Vector2[] uvs = SpriteUtility.GetSpriteUVs(sprite, getAtlasData: true);
          float minX, minY, maxX, maxY;
          minX = maxX = uvs[0].x;
          minY = maxY = uvs[0].y;

          for (int j = 1; j < uvs.Length; j++) {
            minX = Mathf.Min(minX, uvs[j].x);
            minY = Mathf.Min(minY, uvs[j].y);
            maxX = Mathf.Max(maxX, uvs[j].x);
            maxY = Mathf.Max(maxY, uvs[j].y);
          }

          rects[i] = Rect.MinMaxRect(minX, minY, maxX, maxY);
        }
      }
    }
  }

  protected virtual void uploadSpriteTextures() {
    using (new ProfilerSample("Upload Sprite Textures")) {
      foreach (var spriteFeature in _spriteFeatures) {
        var tex = spriteFeature.data.Query().
                                     Select(d => d.sprite).
                                     Where(s => s != null).
                                     Select(s => {
#if UNITY_EDITOR
                                       var atlas = SpriteUtility.GetSpriteTexture(s, getAtlasData: true);

                                       //Cannot reference atlas texture in material at runtime because it doesn't
                                       //actually exist as an asset, so we create a direct copy of the atlas
                                       //instead for preview.  We don't save it, then load the correct atlas 
                                       //at runtime.
                                       var safeAtlas = new Texture2D(atlas.width, atlas.height, atlas.format, atlas.mipmapCount > 1);
                                       safeAtlas.name = "Temp atlas texture";
                                       safeAtlas.hideFlags = HideFlags.HideAndDontSave;
                                       Graphics.CopyTexture(atlas, safeAtlas);
                                       return safeAtlas;
#else
                                       return s.texture;
#endif
                                     }).
                                     FirstOrDefault();

        if (tex != null) {
          _material.SetTexture(spriteFeature.propertyName, tex);
        }
      }
    }
  }
  #endregion

  #region MESH GENERATION
  protected virtual void buildAllElements() {
    beginMesh();
    for (_currIndex = 0; _currIndex < gui.elements.Count; _currIndex++) {
      _currElement = gui.elements[_currIndex];
      buildElement();
    }
    finishMesh();
  }

  protected virtual void buildElement() {
    foreach (var meshFeature in _meshFeatures) {
      var meshData = meshFeature.data[_currIndex];

      refreshMeshData(meshData);
      if (meshData.mesh == null) continue;

      buildTopology(meshData);

      if (_doesRequireColors) {
        buildColors(meshFeature, meshData);
      }

      foreach (var channel in _requiredUvChannels) {
        buildUvs(channel, meshData);
      }

      if (_doesRequireUv3) {
        buildUv3(meshData);
      }
    }

    foreach (var blendShapeFeature in _blendShapeFeatures) {
      buildBlendShapes(blendShapeFeature.data[_currIndex]);
    }
  }

  protected virtual void refreshMeshData(LeapGuiMeshData meshData) {
    meshData.RefreshMeshData();
  }

  protected virtual void buildTopology(LeapGuiMeshData meshData) {
    var topology = MeshCache.GetTopology(meshData.mesh);

    int vertOffset = _verts.Count;
    for (int i = 0; i < topology.tris.Length; i++) {
      _tris.Add(topology.tris[i] = vertOffset);
    }

    for (int i = 0; i < topology.verts.Length; i++) {
      _verts.Add(elementVertToMeshVert(topology.verts[i]));
    }
  }

  protected virtual void buildColors(LeapGuiMeshFeature feature, LeapGuiMeshData meshData) {
    Color totalTint = feature.tint * meshData.tint;
    MeshCache.GetColors(meshData.mesh).Query().Select(c => c * totalTint).AppendList(_colors);
  }

  protected virtual void buildUvs(UVChannelFlags channel, LeapGuiMeshData meshData) {
    var uvs = MeshCache.GetUvs(meshData.mesh, channel);

    Rect[] atlasedUvs;
    if (_packedRects != null &&
       _packedRects.TryGetValue(channel, out atlasedUvs) &&
       (meshData.remappableChannels & channel) != 0) {
      MeshUtil.RemapUvs(uvs, atlasedUvs[_currIndex]);
    }

    _uvs[channel.Index()].AddRange(uvs);
  }

  protected virtual void buildUv3(LeapGuiMeshData meshData) {
    _uvs[3].Append(meshData.mesh.vertexCount, new Vector4(0, 0, 0, _currIndex));
  }

  protected virtual void buildBlendShapes(LeapGuiBlendShapeData blendShapeData) {
    var shape = blendShapeData.blendShape;

    int offset = _verts.Count - shape.vertexCount;

    var verts = shape.vertices;
    for (int i = 0; i < verts.Length; i++) {
      Vector3 shapeVert = verts[i];
      Vector3 delta = elementVertToMeshVert(shapeVert) - _verts[i + offset];

      Vector4 currUv = _uvs[3][i + offset];
      currUv.x = delta.x;
      currUv.y = delta.y;
      currUv.z = delta.z;
      _uvs[3][i + offset] = currUv;
    }
  }

  protected virtual void beginMesh() {
    Assert.IsNull(_currMesh, "Cannot begin a new mesh without finishing the current mesh.");

    _currMesh = new Mesh();
    _currMesh.name = "Procedural Gui Mesh";
    _currMesh.hideFlags = HideFlags.None;

    _verts.Clear();
    _normals.Clear();
    _tris.Clear();
    _colors.Clear();
    for (int i = 0; i < 4; i++) {
      _uvs[i].Clear();
    }
  }

  protected virtual void finishMesh() {
    //If there is no data, don't actually do anything
    if (_verts.Count == 0) {
      DestroyImmediate(_currMesh);
      _currMesh = null;
      return;
    }

    _currMesh.SetVertices(_verts);
    _currMesh.SetTriangles(_tris, 0);

    if (_normals.Count == _verts.Count) {
      _currMesh.SetNormals(_normals);
    }

    if (_colors.Count == _verts.Count) {
      _currMesh.SetColors(_colors);
    }

    for (int i = 0; i < 4; i++) {
      _currMesh.SetUVsAuto(i, _uvs[i]);
    }

    _meshes.Add(_currMesh);
    _currMesh = null;
  }
  #endregion

  #region UTILITY
  protected virtual bool doesRequireMeshColors() {
    return _meshFeatures.Query().Any(f => f.color);
  }

  protected virtual bool doesRequireMeshNormals() {
    return _meshFeatures.Query().Any(f => f.normals);
  }

  protected virtual bool doesRequireUvChannel(UVChannelFlags channel) {
    switch (channel) {
      case UVChannelFlags.UV0:
        return _meshFeatures.Query().Any(f => f.uv0);
      case UVChannelFlags.UV1:
        return _meshFeatures.Query().Any(f => f.uv1);
      case UVChannelFlags.UV2:
        return _meshFeatures.Query().Any(f => f.uv2);
      case UVChannelFlags.UV3:
        return _meshFeatures.Query().Any(f => f.uv3) ||
               _blendShapeFeatures.Count != 0 ||
               _tintFeatures.Count != 0;
      default:
        throw new ArgumentException("Invalid channel argument.");
    }
  }

  protected abstract Vector3 elementVertToMeshVert(Vector3 vertex);
  #endregion
}
