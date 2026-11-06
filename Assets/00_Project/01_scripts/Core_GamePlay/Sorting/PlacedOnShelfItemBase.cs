using UnityEngine;
using DG.Tweening;

/// <summary> Item sitting on a shelf; E picks it back up. </summary>
public class PlacedOnShelfItemBase : MonoBehaviour, IInteractable, IHoverable
{
    [Header("Action Prompt")]
    [Tooltip("Primary prompt verb; empty hides it.")]
    [SerializeField] private string actionPrompt = "Take back";
    [Tooltip("Label above the prompts; empty hides it.")]
    [SerializeField] private string hoverPrompt = "";

    /// <summary> Primary prompt verb. </summary>
    public string ActionPrompt => Localizer.FromEnglish("prompt", actionPrompt);
    /// <summary> Label shown above the prompts. </summary>
    public string HoverPrompt => Localizer.FromEnglish("prompt", hoverPrompt);

    [Header("Place Feedback")]
    [Tooltip("Scale added at the peak of the landing punch.")]
    [SerializeField] private Vector3 punchScale = new Vector3(0.2f, 0.2f, 0.2f);

    [Tooltip("Seconds the landing punch lasts.")]
    [SerializeField] private float punchDuration = 0.25f;

    [SerializeField] private int punchVibrato = 6;
    [SerializeField] private float punchElasticity = 0.5f;

    /// <summary> Sortable data this placed item represents. </summary>
    public SortableItem_SO Data { get; private set; }

    /// <summary> Shelf holding this item, or null while unplaced. </summary>
    public SortingShelf Shelf { get; private set; }

    /// <summary> True once landed; interaction is ignored mid-flight. </summary>
    public bool IsSettled { get; private set; }

    /// <summary> True once flagged as sitting on a wrong-category shelf. </summary>
    public bool IsWrong => _isWrong;
    public bool IsFlashingWrong => _wrongFeedback != null && _wrongFeedback.IsFlashing;

    private static readonly Color HoverOutlineColor = Color.white;
    private MeshRenderer _model;
    private ItemVisualGroup _visualGroup;
    private ItemChunkRenderer.Member _chunk;
    private Tween _punch;
    private Tween _flash;
    private bool _hovered;
    private WrongPlacementFeedback _wrongFeedback;
    private bool _isWrong; // tinted items stay off the chunk merge

    private void Awake()
    {
        _visualGroup = GetComponent<ItemVisualGroup>();
        _model = GetComponentInChildren<MeshRenderer>();
        if (_visualGroup == null) ItemDetail.ApplyTo(_model);

        // Prefabs without the component fall back to its defaults.
        _wrongFeedback = GetComponent<WrongPlacementFeedback>();
        if (_wrongFeedback == null) _wrongFeedback = gameObject.AddComponent<WrongPlacementFeedback>();
        if (_visualGroup != null) _wrongFeedback.Bind(_visualGroup.Renderers);
        else _wrongFeedback.Bind(_model);
    }

    /// <summary> Shows the chosen item detail on this item's own renderer. </summary>
    internal void RefreshDetail(short lod)
    {
        if (_visualGroup != null) _visualGroup.RefreshDetail(lod);
        else if (_model != null && _model.TryGetComponent(out MeshFilter filter) &&
                 filter.sharedMesh != null && filter.sharedMesh.lodCount > 1) _model.forceMeshLod = lod;
    }

    /// <summary> Stamps the data this item was spawned from. </summary>
    internal void Init(SortableItem_SO data) => Data = data;

    /// <summary> Links this item to its shelf; not settled until it lands. </summary>
    internal void AttachTo(SortingShelf shelf)
    {
        Shelf = shelf;
        IsSettled = false;
    }

    /// <summary> Marks the item landed; it merges into a item chunk once still. </summary>
    internal void Settle()
    {
        IsSettled = true;
        if (_chunk == null && !_isWrong)
            _chunk = _visualGroup != null
                ? ItemChunkRenderer.Register(_visualGroup, transform, false)
                : ItemChunkRenderer.Register(_model, transform, false);
    }

    /// <summary> Tints the item wrong; with flash it also plays the buzz. </summary>
    public void PlayWrongFeedback(bool flash = true)
    {
        _isWrong = true;
        ItemChunkRenderer.Unregister(_chunk); // merged cells can't show a per-item tint
        _chunk = null;
        if (_visualGroup == null && _model != null) _model.enabled = true; // groups restore their own

        _wrongFeedback.PlayTint(flash);
        if (flash) _wrongFeedback.PlaySound();
    }

    /// <summary> Shows the wrong-section text once the item has landed. </summary>
    public void PlayWrongLanded()
    {
        if (_isWrong) _wrongFeedback.PlayText();
    }

    /// <summary> Celebrates a correct placement after landing. </summary>
    public void PlayCorrectLanded()
    {
        Color green = new Color(0.25f, 1f, 0.4f);
        FlashOutline(green, 0.4f);
        Transform shelf = Shelf != null ? Shelf.transform : transform;
        Vector3 start = transform.position + Vector3.up * 0.55f - shelf.forward * 0.45f;
        FloatingTextEffect.ShowKey("feedback.great_job", start, green);
    }

    /// <summary> Shows the item on its own renderer while a tween animates it. </summary>
    public void DetachForAnimation() => ItemChunkRenderer.Detach(_chunk);

    /// <summary> Punches the scale up on landing. </summary>
    public virtual void OnPlaceFeedback()
    {
        _punch?.Complete(); // snap a running punch back to base scale
        DetachForAnimation();
        _punch = transform.DOPunchScale(punchScale, punchDuration, punchVibrato, punchElasticity);
    }

    /// <summary> Shows the outline in a color for a while, then restores the old color. </summary>
    public void FlashOutline(Color color, float duration)
    {
        _flash?.Kill();

        if (_visualGroup != null) _visualGroup.ShowPrimary(color);
        else SharedOutline.Show(this, _model, color);
        _flash = DOVirtual.DelayedCall(duration, EndFlash, false).SetLink(gameObject);
    }

    /// <summary> Restores the hover color; outline stays on only while hovered. </summary>
    private void EndFlash()
    {
        _flash = null;
        SetOutline(_hovered && IsSettled);
    }

    /// <summary> Picks this item back up (E). F falls through to the parent shelf. </summary>
    public void OnInteract(InteractionHandler handler)
    {
        if (!IsSettled || Shelf == null) return;

        Shelf.TryTakeBack(this);
    }

    /// <inheritdoc />
    public void OnHoverEnter()
    {
        _hovered = true;
        if (_flash == null) SetOutline(IsSettled);
        Shelf?.RefreshPlacementPreview();
    }

    /// <inheritdoc />
    public void OnHoverExit()
    {
        _hovered = false;
        if (_flash == null) SetOutline(false);
        Shelf?.ClearPlacementPreview();
    }

    /// <inheritdoc />
    public void OnHoverStay() => Shelf?.RefreshPlacementPreview();

    private void SetOutline(bool visible)
    {
        if (_visualGroup != null)
        {
            if (visible) _visualGroup.ShowPrimary(HoverOutlineColor);
            else _visualGroup.HidePrimary();
        }
        else if (visible) SharedOutline.Show(this, _model, HoverOutlineColor);
        else SharedOutline.Hide(this);
    }

    private void OnDestroy()
    {
        transform.DOKill();
        if (_visualGroup != null) _visualGroup.HidePrimary();
        else SharedOutline.Hide(this);
        ItemChunkRenderer.Unregister(_chunk);
        _chunk = null;
    }
}
