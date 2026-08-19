using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Тонкая сетевая оболочка игрока (этап 1–2).
/// Вешать на префаб игрока рядом с PlayerMovement3D / CombatController3D.
/// Нужны: NetworkObject (+ по желанию NetworkTransform).
///
/// - Владелец: включает локальный ввод, цепляет камеру.
/// - Чужой клиент: отключает ввод движения (пока без полного синка анимаций).
/// - Без сети (нет NetworkManager / не заспавнен) — ничего не ломает, одиночка жива.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetworkPlayer : NetworkBehaviour
{
    [Header("Ссылки (можно оставить пустыми — найдёт на этом объекте)")]
    public PlayerMovement3D movement;
    public CameraFollow cameraFollow;

    [Tooltip("Искать CameraFollow на сцене, если не задан.")]
    public bool autoFindCamera = true;

    public bool IsLocalControlled { get; private set; }

    void Awake()
    {
        if (movement == null)
            movement = GetComponent<PlayerMovement3D>();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        IsLocalControlled = IsOwner;

        // Точку спавна применяем на сервере (Host), чтобы все видели одну позицию.
        if (IsServer)
            ApplySpawnPoint();

        EnsureRegistry().Register(transform);

        ApplyControlState();

        if (IsOwner)
            BindCamera();
    }

    void ApplySpawnPoint()
    {
        if (!PlayerSpawnPoint.TryGetSpawnPose(out Vector3 pos, out Quaternion rot))
            return;

        var cc = GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;
        transform.SetPositionAndRotation(pos, rot);
        if (cc != null) cc.enabled = true;

        // Если висит NetworkTransform — форсим синк после телепорта.
        var nt = GetComponent<Unity.Netcode.Components.NetworkTransform>();
        if (nt != null)
            nt.Teleport(pos, rot, transform.localScale);
    }

    public override void OnNetworkDespawn()
    {
        if (PlayerRegistry.Instance != null)
            PlayerRegistry.Instance.Unregister(transform);

        base.OnNetworkDespawn();
        // Камеру не обнуляем жёстко — сцена может перезагрузиться.
    }

    static PlayerRegistry EnsureRegistry()
    {
        if (PlayerRegistry.Instance != null)
            return PlayerRegistry.Instance;

        var go = new GameObject("PlayerRegistry");
        return go.AddComponent<PlayerRegistry>();
    }

    void ApplyControlState()
    {
        // Пока нет отдельного флага в PlayerMovement3D — просто выключаем компонент у чужих.
        // Владелец и одиночка (если когда-то заспавнят без owner-логики) ходят как обычно.
        if (movement != null)
            movement.enabled = IsOwner || !IsSpawned;
    }

    void BindCamera()
    {
        if (cameraFollow == null && autoFindCamera)
            cameraFollow = FindObjectOfType<CameraFollow>();

        if (cameraFollow != null)
            cameraFollow.target = transform;
        else
            Debug.LogWarning("NetworkPlayer: CameraFollow не найден — камера не привязана.");
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (movement == null)
            movement = GetComponent<PlayerMovement3D>();
    }
#endif
}
