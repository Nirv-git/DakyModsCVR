using ABI.CCK.Components;
using ABI_RC.Core.Base;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using B83.Win32;
using Daky;
using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using Bitmap = System.Drawing.Bitmap;
using LfsApi = LagFreeScreenshots.API.LfsApi;
using PortableCamera = ABI_RC.Systems.Camera.PortableCamera;

[assembly: MelonGame("Alpha Blend Interactive", "ChilloutVR")]
[assembly: MelonInfo(typeof(CameraInstants.CameraInstantsMod), "CameraInstants", "2.0.0", "daky", "https://github.com/dakyneko/DakyModsCVR")]
[assembly:MelonAdditionalDependencies("LagFreeScreenshots")]
[assembly:MelonOptionalDependencies("libwebpwrapper")]

namespace CameraInstants;

public class CameraInstantsMod : MelonMod
{
    private static MelonLogger.Instance logger;
    private MelonPreferences_Entry<bool> myInstantsEnabled, captureAutoPropUpload, autoSpawnProp;
    private MelonPreferences_Entry<float> autoSpawnPropSize;
    private MelonPreferences_Entry<string> uploadUsername, uploadKey;
    private Queue<string> autoSpawnPropsGids = new();
    private static bool isWebPInstalled = false;

    public override void OnInitializeMelon()
    {
        logger = LoggerInstance;

        var category = MelonPreferences.CreateCategory("CameraInstants", "CameraInstants");
        myInstantsEnabled = category.CreateEntry("InstantsEnabled", true, "Spawn instants locally", "When shooting with the camera, spawn the image in the world (local only)");
        captureAutoPropUpload = category.CreateEntry("CaptureAutoPropUpload", false, "Instants props", "When shooting with the camera, spawn the image in the world (for everybody). This builds a prop and upload it to CVR (requires upload username and key).");
        autoSpawnProp = category.CreateEntry("AutoSpawnProp", false, "Spawn instant props", "Spawn instants props automatically");
        autoSpawnPropSize = category.CreateEntry("AutoSpawnPropSize", 0.6f, "Size of Instant props", "Maximum length (width or height) in game dimension");
        uploadUsername = category.CreateEntry("UploadUserName", "", "CCK Username", "Necessary for instants props");
        uploadKey = category.CreateEntry("UploadKey", "", "CCK Key", "Necessary for instants props");
        // TODO: should listen to events on myInstantsEnabled change and add/rem listener instead
        LfsApi.OnScreenshotTexture += OnScreenshotTexture;
        LfsApi.OnScreenshotSavedV2 += OnScreenshotSaved;

        // TODO: support piles of pictures (multiple stacked on each other)
        // either we can spread them or put them between them, latest should be always on top?
        // TODO: can add options to camera settings panel
        // - spawn position: top, bottom, left, right
        // - spawn size, transparency, resolution
        // - allowed action like delete
        // TODO: implement delete on disk (move into trash bin)

        HarmonyInstance.Patch(
            SymbolExtensions.GetMethodInfo(() => default(MetaPort).Awake()),
            new HarmonyMethod(AccessTools.Method(typeof(CameraInstantsMod), nameof(MetaPortAwake))));
        UnityDragAndDropHook.OnDroppedFiles += OnDropFiles;

        isWebPInstalled = LagFreeScreenshots.WebpUtils.IsWebpSupported();

        // Check for BTKUILib and add settings UI
        if (RegisteredMelons.Any(m => m.Info.Name == "BTKUILib"))
            Daky.DakyBTKUI.AutoGenerateCategory(category);
    }

    public static void MetaPortAwake(MetaPort __instance)
    {
        // receiving windows events is overwhelming, let's only add it when unfocused = file drop may happen
        var cb = __instance.gameObject.AddComponentIfMissing<OnApplicationFocusCallback>();
        cb.onFocus += focus => { if (focus) UnityDragAndDropHook.UninstallHook(); else UnityDragAndDropHook.InstallHook(); };
    }

    public override void OnApplicationQuit()
    {
        UnityDragAndDropHook.UninstallHook(); // just in case
    }

    // Warning: if we throw an exception in this win32 callback, the game will crash
    // therefore we protect from everything, just in case
    private void OnDropFiles(List<string> paths, POINT point)
    {
        try
        {
            OnDropFilesInternal(paths, point);
        }
        catch (Exception ex)
        {
            logger.Error(ex);
        }
    }

    private void CheckUploadConfiguration()
    {
        if (uploadUsername.Value == "" || uploadKey.Value == "")
            throw new Exception($"Auto prop upload cannot work without CCK settings");
    }

    private UploadTask CreateUploadTask(string? propName = null)
    {
        return new UploadTask()
        {
            propName = propName ?? ("Picture " + DateTime.Now.ToString("dd MMM, HH:mm:ss")),
            propDesc = "unattended instants upload",
            username = uploadUsername.Value,
            key = uploadKey.Value,
        };
    }

    private void OnDropFilesInternal(List<string> paths, POINT point)
    {
        if (paths.Count == 0) return;
        if (paths.Count > 1)
        {
            logger.Warning($"OnDropFiles allows only 1 file for now");
            return;
        }

        var filepath = paths[0];
        var mime = System.Web.MimeMapping.GetMimeMapping(filepath);
        var supported = mime switch
        {
            "image/jpeg" or "image/png" or "image/bmp" => true,
            "image/webp" => isWebPInstalled,
            "application/octet-stream" => filepath.EndsWith(".webp") && isWebPInstalled, // seems .net is stupid and doesn't detect webp properly
            // TODO: add gifs, maybe tiff?
            _ => false,
        };
        if (!supported)
        {
            logger.Warning($"Format {mime} ({Path.GetExtension(filepath)}) probably not supported");
            return;
        }

        logger.Msg($"OnDropFiles start AsyncPropUploader {filepath} (mime: {mime}, extension: {Path.GetExtension(filepath)})");
        var propName = Path.ChangeExtension(Path.GetFileName(filepath), "");
        CheckUploadConfiguration();
        Task.Run(() => AutoPropTask(filepath, propName));
    }

    private void OnScreenshotTexture(RenderTexture rtex)
    {
        var portableCamera = PortableCamera.Instance;
        if (!myInstantsEnabled.Value || portableCamera == null) return;

        // let's downscale the instants texture to save memory
        var aspectRatio = 1f * rtex.height / rtex.width;
        int w = 640; // TODO: make this a setting
        int h = Mathf.FloorToInt(w * aspectRatio);
        var rtex2 = RenderTexture.GetTemporary(w, h, rtex.depth, rtex.format);
        RenderTexture.active = rtex2;
        GL.sRGBWrite = true; // needed to keep colors+brightness
        Graphics.Blit(rtex, rtex2);

        var tex = new Texture2D(w, h, TextureFormat.ARGB32, false); // we're restricted to RGBA because GPU = RenderTexture are stuck with RGBA
        tex.filterMode = FilterMode.Bilinear;
        Graphics.CopyTexture(rtex2, tex);
        RenderTexture.ReleaseTemporary(rtex2);
        RenderTexture.active = null;

        var plane = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var t = plane.transform;
        t.SetParent(portableCamera.transform.parent, false); // to CVR Camera 2.0
        t.localPosition = 150 * Vector3.left;
        t.localRotation = Quaternion.Euler(0, 0, 180);
        t.localScale = new Vector3(140f, 140f * aspectRatio, 1f);

        // make it double sided because easy to lose it in the world
        var backside = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var t2 = backside.transform;
        t2.SetParent(t, false);
        t2.localRotation = Quaternion.Euler(0, 180, 0); // backside

        var m = new Material(Shader.Find("Unlit/Texture"));
        m.mainTexture = tex;
        plane.GetComponent<Renderer>().material = m;
        backside.GetComponent<Renderer>().material = m;

        plane.name = "CameraInstants";
        backside.name = "back";
        plane.layer = LayerMask.NameToLayer("UI");
        backside.GetComponent<Collider>().enabled = false;

        var body = plane.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = true;
        var pickup = plane.AddComponent<CVRPickupObject>();
        pickup.gripType = CVRPickupObject.GripType.Free;
        bool grabbed = false;
        pickup.drop.AddListener(() => grabbed = false);
        pickup.grab.AddListener(() =>
        {
            t.SetParent(null, true);
            grabbed = true;
        });

        var interactable = plane.AddComponent<CVRInteractable>();
        interactable.actions = new() {
            new() {
                actionType = CVRInteractableAction.ActionRegister.OnInteractDown,
                execType = CVRInteractableAction.ExecutionType.LocalNotNetworked,
                operations = new() {
                    new CVRInteractableActionOperation {
                        type = CVRInteractableActionOperation.ActionType.MethodCall,
                        gameObjectVal = plane,
                        customEvent = UnityEventWithAction(() => {
                            if (grabbed)
                                GameObject.Destroy(plane);
                        }),
                    },
                },
            }
        };
    }

    private static UnityEvent UnityEventWithAction(UnityAction f)
    {
        var ev = new UnityEvent();
        ev.AddListener(f);
        return ev;
    }

    private void OnScreenshotSaved(string filepath, int width, int height, LagFreeScreenshots.API.MetadataV2? metadata) {
        if (!captureAutoPropUpload.Value) return;

        logger.Msg($"OnScreenshotSaved start AutoPropTask {filepath}");
        CheckUploadConfiguration();
        Task.Run(() => AutoPropTask(filepath));
    }

    private async Task AutoPropTask(string imagePath, string? propName = null)
    {
        try { await AutoPropTask_(imagePath, propName); }
        catch (Exception e) { logger.Error($"Error in AutoPropTask: {e}"); }
    }

    private async Task AutoPropTask_(string imagePath, string? propName = null)
    {
        var watch = new Stopwatch();
        watch.Start();
        logger.Msg($"AutoPropTask starting");
        var upload = CreateUploadTask(propName);
        upload.gid = await InstantsPropUploader.NewPropGid(upload);
        logger.Msg($"Upload step #1 got gid ({watch.ElapsedMilliseconds} msec)");
        watch.Restart();

        var ns = this.GetType().Namespace + ".Resources";
        var templateBundle = Dakytils.StreamFromAssembly(ns, "cvrspawnable_00000000-0000-0000-0000-000000000000.cvrprop");
        if (templateBundle == null) throw new Exception($"Missing bundle template");

        using var bitmap = imagePath.EndsWith(".webp") ?
            LoadWebP(imagePath) :
            new Bitmap(imagePath);
        // TODO: should resize the image so it uploads faster (CVR API is slow)
        logger.Msg($"Upload step #2 make thumbnail + build prop");
        var thumbnail = InstantsPropBuilder.MakeThumbnail(bitmap, ImageFormat.Jpeg);
        logger.Msg($"Made thumbnail ({watch.ElapsedMilliseconds} msec)");
        watch.Restart();
        var bundle = InstantsPropBuilder.Build(templateBundle, bitmap, upload.gid, propSize: autoSpawnPropSize.Value);
        bitmap.Dispose();

        logger.Msg($"Upload step #3 upload ({watch.ElapsedMilliseconds} msec)");
        watch.Restart();
        upload.bundle = bundle;
        upload.thumbnail = thumbnail;
        logger.Msg($"temp file: bundle={bundle} thumbnail={thumbnail} ({watch.ElapsedMilliseconds} msec)");
        watch.Restart();
        await InstantsPropUploader.UploadPropBundle(upload);
        File.Delete(upload.bundle); // cleaning up
        File.Delete(upload.thumbnail);

        logger.Msg($"Queue prop for spawning");
        autoSpawnPropsGids.Enqueue(upload.gid);

        logger.Msg($"AutoPropTask done ({watch.ElapsedMilliseconds} msec)");
    }

    // WebPWrapper may not be installed, so we need to isolate it
    private static Bitmap LoadWebP(string imagePath) => new WebPWrapper.WebP().Load(imagePath);

    public override void OnUpdate()
    {
        if (!autoSpawnProp.Value) return;
        if (autoSpawnPropsGids.Count == 0) return;

        var gid = autoSpawnPropsGids.Dequeue();
        logger.Msg($"Spawning auto-uploaded image prop: {gid}");
        PlayerSetup.Instance.DropProp(gid);
    }
}

public class OnApplicationFocusCallback : MonoBehaviour
{
    public Action<bool> onFocus = null;

    private void OnApplicationFocus(bool focus) => onFocus?.Invoke(focus);
}

public class UploadTask
{
    public string gid, propName, propDesc, username, key;
    public string bundle, thumbnail;
}
