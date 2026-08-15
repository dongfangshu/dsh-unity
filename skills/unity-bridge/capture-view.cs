public static class Entry {
  public static object Main(object args) {
    string dir = Path.GetFullPath(Path.Combine(
      Application.dataPath, "..", "Library", "UnityBridge", "status"));
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, "view.png");

    Camera cam = null;
    int w = 1280, h = 720;

    if (EditorApplication.isPlaying) {
      cam = Camera.main;
      if (cam == null) {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>()) {
          if (c != null && c.enabled && c.gameObject.activeInHierarchy) {
            cam = c;
            break;
          }
        }
      }
      if (cam != null) {
        w = Mathf.Max(16, cam.pixelWidth);
        h = Mathf.Max(16, cam.pixelHeight);
      }
    }

    if (cam == null) {
      var sv = SceneView.lastActiveSceneView;
      if (sv == null)
        throw new Exception("no camera (open a Scene View, or play with a Camera)");
      cam = sv.camera;
      w = Mathf.Max(16, (int)sv.position.width);
      h = Mathf.Max(16, (int)sv.position.height);
    }

    var rt = new RenderTexture(w, h, 24);
    var prev = cam.targetTexture;
    cam.targetTexture = rt;
    cam.Render();
    RenderTexture.active = rt;
    var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
    tex.Apply();
    cam.targetTexture = prev;
    RenderTexture.active = null;

    File.WriteAllBytes(path, tex.EncodeToPNG());
    UnityEngine.Object.DestroyImmediate(tex);
    rt.Release();
    UnityEngine.Object.DestroyImmediate(rt);

    return new Dictionary<string, object> {
      ["kind"] = "image",
      ["path"] = path
    };
  }
}
