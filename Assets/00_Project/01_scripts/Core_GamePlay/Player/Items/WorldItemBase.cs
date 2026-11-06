using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

/// <summary> Item resting in the world. Always carryable: hover to highlight, interact to stash and equip. </summary>
public class WorldItemBase : MonoBehaviour, IInteractable, IHoverable
{
    [Header("Action Prompt")]
    [Tooltip("Primary prompt verb; empty hides it.")]
    [SerializeField] private string actionPrompt = "Pick up";
    [Tooltip("Label above the prompts; empty hides it.")]
    [SerializeField] private string hoverPrompt = "";

    /// <summary> Primary prompt verb. </summary>
    public string ActionPrompt => Localizer.FromEnglish("prompt", actionPrompt);
    /// <summary> Label shown above the prompts. </summary>
    public string HoverPrompt => Localizer.FromEnglish("prompt", hoverPrompt);

    [Tooltip("Data linking this item to its hand form.")]
    [SerializeField] private ItemDataSO data;

    [Tooltip("Stable scene copy id. Assigned to each authored car by the store builder.")]
    [SerializeField] private int authoredSaveId;

    [Header("Feedback")]
    [Tooltip("Optional prompt shown while the reticle rests on the item.")]
    [SerializeField] private GameObject hoverHint;

    /// <summary> Data describing this item and its hand form. </summary>
    public ItemDataSO Data => data;

    /// <summary>Stable id for a scene-authored copy, or zero for legacy/spawned items.</summary>
    public int AuthoredSaveId => authoredSaveId;

    private static readonly List<WorldItemBase> AllItems = new List<WorldItemBase>();

    /// <summary> Every enabled world item; registered in OnEnable. </summary>
    public static IReadOnlyList<WorldItemBase> All => AllItems;

    /// <summary> This item's Rigidbody; kinematic while frozen by physics culling. </summary>
    public Rigidbody Body { get; private set; }

    /// <summary> Handle in the item chunk renderer, or null if not merged. </summary>
    public ItemChunkRenderer.Member ChunkMember { get; private set; }

    /// <summary> Set by physics culling while the body moves. </summary>
    internal bool WasMoving { get; set; }

    /// <summary> True once physics moved it; the save then stores its pose. </summary>
    internal bool Moved { get; set; }

    /// <summary> True for items placed in the scene rather than spawned. </summary>
    internal bool IsAuthored { get; set; }

    /// <summary> True while enabled and listed in All. </summary>
    internal bool IsRegistered => _allIndex >= 0;

    /// <summary> True while physics culling holds it kinematic. </summary>
    internal bool Frozen { get; set; }

    /// <summary> Position cached while frozen; frozen items never move. </summary>
    internal Vector3 RestPosition { get; set; }

    /// <summary> True while the throw pull tween drives it; culling leaves it alone. </summary>
    internal bool ThrowControlled { get; set; }

    /// <summary> Power currently tweening this item, or null. </summary>
    internal object PowerOwner { get; private set; }

    private MeshRenderer _model;
    private ItemVisualGroup _visualGroup;
    private int _allIndex = -1;
    private bool _started;
    private Collider[] _colliders;
    private bool[] _authoredTrigger;
    private Tween _powerTween;
    private readonly object _powerOutlineKey = new object(); // separate from the hover outline
    private readonly Collider[] _releaseHits = new Collider[128];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => AllItems.Clear();

    protected virtual void Awake()
    {
        _visualGroup = GetComponent<ItemVisualGroup>();
        _model = GetComponentInChildren<MeshRenderer>();
        if (_visualGroup == null) ItemDetail.ApplyTo(_model); // the old single-mesh item path
        Body = GetComponent<Rigidbody>();
        // These native solver properties are not serialized in Rigidbody prefabs.
        if (Body != null && TryGetComponent<CapsuleCollider>(out var capsule))
        {
            TinyRiders.LooseCarPhysics.Configure(Body);
            capsule.contactOffset = .002f;
        }
        Frozen = Body != null && Body.isKinematic; // prefab items start kinematic
        RestPosition = transform.position;
        ShowHint(false);
    }

    protected virtual void OnEnable()
    {
        _allIndex = AllItems.Count;
        AllItems.Add(this);
        if (_started) RegisterChunk();
    }

    // Start, so a thrown copy already got its body and counts as moving.
    protected virtual void Start()
    {
        _started = true;
        RestPosition = transform.position; // restored copies are placed after Awake
        RegisterChunk();
    }

    private void OnCollisionEnter(Collision collision)
    {
        // A thrown or pushed car can hit frozen stock outside the player's active
        // radius. Wake its neighbours before the next physics step so the impact
        // can propagate through the pile.
        if (Body == null || Body.isKinematic || Body.IsSleeping() ||
            collision.relativeVelocity.sqrMagnitude < 0.25f) return;
        // Landing on the floor is not an impact on stock. Waking a radius on
        // every landing repeatedly disturbs cars that were already settled.
        if (collision.collider.GetComponentInParent<WorldItemBase>() == null) return;
        if (collision.contactCount > 0)
            ItemPhysicsCulling.WakeAround(collision.GetContact(0).point);
    }

    protected virtual void OnDisable()
    {
        RemoveFromAll();
        SetOutline(false);
        ClearPowerControl();
        _releaseImpulse = _releaseTorque = Vector3.zero;
        ItemChunkRenderer.Unregister(ChunkMember);
        ChunkMember = null;
        LevelSaveManager.MarkDirty();
    }

    /// <summary> Hands the item to a power: kinematic, detached, previous power's tween killed. </summary>
    internal void BeginPowerControl(object owner, bool asTrigger)
    {
        bool wasControlled = PowerOwner != null;
        _releasePending = false;
        _releaseImpulse = _releaseTorque = Vector3.zero;
        PowerOwner = owner;
        KillPowerTween(); // the old owner sees it lost control

        if (!wasControlled)
        {
            ItemPhysicsCulling.WakeAround(transform.position); // items resting on it fall
            ItemChunkRenderer.Detach(ChunkMember); // chunk can't show it moving
            ThrowControlled = true; // culling leaves it alone
            Frozen = false;
            if (Body != null) Body.isKinematic = true;
        }

        // Scripted flight must not inject kinematic impulses into the pile.
        // Pick queries include triggers, so floating cars remain selectable.
        SetCollidersTrigger(true);
    }

    /// <summary> Stores the tween driving this item, killed if another power takes over. </summary>
    internal void SetPowerTween(Tween tween) => _powerTween = tween;

    /// <summary> Releases the item from its power and drops it at a downward speed. </summary>
    internal void EndPowerControl(float dropSpeed)
    {
        if (PowerOwner == null) return;

        _releaseSpeed = dropSpeed;
        if (!TryFindReleasePose()) return; // Retry next physics step if fully enclosed.
        ClearPowerControl();
        Moved = true;
        if (Body == null) return;

        Body.isKinematic = false;
        Body.WakeUp();
        Body.linearVelocity = Vector3.down * dropSpeed; // still bodies get frozen mid-air by culling
        Body.angularVelocity = Vector3.zero;
        Body.AddForce(_releaseImpulse, ForceMode.Impulse);
        Body.AddTorque(_releaseTorque, ForceMode.Impulse);
        _releaseImpulse = _releaseTorque = Vector3.zero;
        ItemPhysicsCulling.Track(this); // culling freezes it once it lands
    }

    private bool _releasePending;
    private float _releaseSpeed;
    private Vector3 _releaseImpulse, _releaseTorque;

    internal void EndThrowControl(Vector3 impulse, Vector3 torque)
    {
        _releaseImpulse = impulse;
        _releaseTorque = torque;
        EndPowerControl(0f);
    }

    private void FixedUpdate()
    {
        if (_releasePending) EndPowerControl(_releaseSpeed);
    }

    private bool TryFindReleasePose()
    {
        var capsule = GetComponent<CapsuleCollider>();
        if (capsule == null) return true;
        Vector3 original = transform.position;
        Vector3 position = original;
        for (int pass = 0; pass < 16; pass++)
        {
            bool clear = true;
            int count = Physics.OverlapSphereNonAlloc(position + transform.rotation * capsule.center,
                capsule.height * .5f + .05f, _releaseHits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Collider other = _releaseHits[i];
                if (other.attachedRigidbody == Body || other.transform.IsChildOf(transform)) continue;
                if (!Physics.ComputePenetration(capsule, position, transform.rotation,
                    other, other.transform.position, other.transform.rotation, out Vector3 direction, out float distance)) continue;
                position += direction * (distance + .003f);
                clear = false;
            }
            if (clear && count < _releaseHits.Length && (position - original).sqrMagnitude <= 1f)
            {
                transform.position = position;
                if (Body != null) Body.position = position;
                _releasePending = false;
                return true;
            }
        }
        _releasePending = true;
        return false;
    }

    /// <summary> Shows or hides the power outline, separate from the hover one. </summary>
    internal void SetPowerOutline(bool visible, Color color, float width = SharedOutline.Width)
    {
        // Keep highlight feedback white while preserving the power's opacity.
        color = new Color(1f, 1f, 1f, color.a);
        if (_visualGroup != null)
        {
            if (visible) _visualGroup.ShowSecondary(color, width);
            else _visualGroup.HideSecondary();
        }
        else if (visible) SharedOutline.Show(_powerOutlineKey, _model, color, width);
        else SharedOutline.Hide(_powerOutlineKey);
    }

    /// <summary> Drops ownership, outline and trigger state without touching the body. </summary>
    private void ClearPowerControl()
    {
        if (PowerOwner == null) return;

        PowerOwner = null;
        _releasePending = false;
        KillPowerTween();
        SetPowerOutline(false, default);
        SetCollidersTrigger(false);
        ThrowControlled = false;
    }

    private void KillPowerTween()
    {
        Tween tween = _powerTween;
        _powerTween = null;
        tween?.Kill();
    }

    /// <summary> Forces every collider to trigger, or restores the authored setting. </summary>
    private void SetCollidersTrigger(bool trigger)
    {
        if (_colliders == null)
        {
            _colliders = GetComponentsInChildren<Collider>(true);
            _authoredTrigger = new bool[_colliders.Length];
            for (int i = 0; i < _colliders.Length; i++) _authoredTrigger[i] = _colliders[i].isTrigger;
        }

        for (int i = 0; i < _colliders.Length; i++)
            if (_colliders[i] != null) _colliders[i].isTrigger = trigger || _authoredTrigger[i];
    }

    /// <summary> Shows the chosen item detail on this item's own renderer. </summary>
    internal void RefreshDetail(short lod)
    {
        if (_visualGroup != null) _visualGroup.RefreshDetail(lod);
        else if (_model != null && _model.TryGetComponent(out MeshFilter filter) &&
                 filter.sharedMesh != null && filter.sharedMesh.lodCount > 1) _model.forceMeshLod = lod;
    }

    private void RegisterChunk()
    {
        if (ChunkMember != null) return;
        bool atRest = Body == null || (Body.isKinematic && !ThrowControlled);
        ChunkMember = _visualGroup != null
            ? ItemChunkRenderer.Register(_visualGroup, transform, atRest) // nested car: every mergeable part
            : ItemChunkRenderer.Register(_model, transform, atRest);
    }

    /// <summary> Swap-removes this item from the registry. </summary>
    private void RemoveFromAll()
    {
        int index = _allIndex;
        if (index < 0 || index >= AllItems.Count || AllItems[index] != this) return;

        int last = AllItems.Count - 1;
        AllItems[index] = AllItems[last];
        AllItems[index]._allIndex = index;
        AllItems.RemoveAt(last);
        _allIndex = -1;
    }

    /// <summary> Highlights the item as the reticle lands on it. </summary>
    public virtual void OnHoverEnter()
    {
        ShowHint(true);
        SetOutline(true);
    }

    /// <summary> Clears the highlight as the reticle leaves. </summary>
    public virtual void OnHoverExit()
    {
        ShowHint(false);
        SetOutline(false);
    }

    /// <inheritdoc />
    public virtual void OnHoverStay() { }

    /// <summary> Stashes and equips the item; refused only when the stash is full. </summary>
    public virtual void OnInteract(InteractionHandler handler)
    {
        Player player = Player.Instance;
        if (player == null || player.StashHandler == null || player.ItemCollector == null || data == null) return;

        if (!player.ItemCollector.TryCollectItem(this))
        {
            return;
        }

        ItemPhysicsCulling.WakeAround(transform.position); // items resting on it fall
        // ItemCollector owns disposal or pooling after a successful pickup.
    }

    private void ShowHint(bool visible)
    {
        if (hoverHint != null) hoverHint.SetActive(visible);
    }

    private void SetOutline(bool visible)
    {
        if (_visualGroup != null)
        {
            if (visible) _visualGroup.ShowPrimary(Color.white);
            else _visualGroup.HidePrimary();
        }
        else if (visible) SharedOutline.Show(this, _model, Color.white);
        else SharedOutline.Hide(this);
    }

}
