// Designed by KINEMATION, 2023

using System;
using Kinemation.FPSFramework.Runtime.FPSAnimator;
using Kinemation.FPSFramework.Runtime.Layers;
using Kinemation.FPSFramework.Runtime.Recoil;

using UnityEngine;
using System.Collections.Generic;
using Random = UnityEngine.Random;

namespace Demo.Scripts.Runtime
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public class TabAttribute : PropertyAttribute
    {
        public readonly string tabName;

        public TabAttribute(string tabName)
        {
            this.tabName = tabName;
        }
    }
    
    public enum FPSAimState
    {
        None,
        Ready,
        Aiming,
        PointAiming
    }

    public enum FPSActionState
    {
        None,
        Reloading,
        WeaponChange
    }

    // An example-controller class
    public class FPSController : FPSAnimController
    {
        [Tab("Animation")] 
        [Header("General")] 
        [SerializeField] private Animator animator;

        [Header("Turn In Place")]
        [SerializeField] private float turnInPlaceAngle;
        [SerializeField] private AnimationCurve turnCurve = new AnimationCurve(new Keyframe(0f, 0f));
        [SerializeField] private float turnSpeed = 1f;

        [Header("Leaning")] 
        [SerializeField] private float smoothLeanStep = 1f;
        [SerializeField, Range(0f, 1f)] private float startLean = 1f;
        
        [Header("Dynamic Motions")]
        [SerializeField] private IKAnimation aimMotionAsset;
        [SerializeField] private IKAnimation leanMotionAsset;
        [SerializeField] private IKAnimation crouchMotionAsset;
        [SerializeField] private IKAnimation unCrouchMotionAsset;
        [SerializeField] private IKAnimation onJumpMotionAsset;
        [SerializeField] private IKAnimation onLandedMotionAsset;
        [SerializeField] private IKAnimation onStartStopMoving;
        
        [SerializeField] private IKPose sprintPose;
        [SerializeField] private IKPose pronePose;

        // Animation Layers
        [SerializeField] [HideInInspector] private LookLayer lookLayer;
        [SerializeField] [HideInInspector] private AdsLayer adsLayer;
        [SerializeField] [HideInInspector] private SwayLayer swayLayer;
        [SerializeField] [HideInInspector] private LocomotionLayer locoLayer;
        [SerializeField] [HideInInspector] private SlotLayer slotLayer;
        [SerializeField] [HideInInspector] private WeaponCollision collisionLayer;
        // Animation Layers
        
        [Header("General")] 
        [Tab("Controller")]
        [SerializeField] private float timeScale = 1f;
        [SerializeField, Min(0f)] private float equipDelay = 0f;

        [Header("Camera")]
        [SerializeField] private Transform mainCamera;
        [SerializeField] private Transform cameraHolder;
        [SerializeField] private Transform firstPersonCamera;
        [SerializeField] private float sensitivity;
        [SerializeField] private Vector2 freeLookAngle;

        [Header("Movement")] 
        [SerializeField] private FPSMovement movementComponent;
        
        [SerializeField] [Tab("Weapon")] 
        private List<Weapon> weapons;
        private Vector2 _playerInput;

        // Used for free-look
        private Vector2 _freeLookInput;

        private int _currentWeaponIndex;
        private int _lastIndex;
        
        private int _bursts;
        private bool _freeLook;
        
        private FPSAimState aimState;
        private FPSActionState actionState;
        
        private static readonly int Crouching = Animator.StringToHash("Crouching");
        private static readonly int OverlayType = Animator.StringToHash("OverlayType");
        private static readonly int TurnRight = Animator.StringToHash("TurnRight");
        private static readonly int TurnLeft = Animator.StringToHash("TurnLeft");
        private static readonly int UnEquip = Animator.StringToHash("UnEquip");

        private Vector2 _controllerRecoil;
        private float _recoilStep;
        private bool _isFiring;

        private bool _isUnarmed;
        private float _lastRecoilTime;

        private void InitLayers()
        {
            InitAnimController();
            
            animator = GetComponentInChildren<Animator>();
            lookLayer = GetComponentInChildren<LookLayer>();
            adsLayer = GetComponentInChildren<AdsLayer>();
            locoLayer = GetComponentInChildren<LocomotionLayer>();
            swayLayer = GetComponentInChildren<SwayLayer>();
            slotLayer = GetComponentInChildren<SlotLayer>();
            collisionLayer = GetComponentInChildren<WeaponCollision>();
        }

        private bool HasActiveAction()
        {
            return actionState != FPSActionState.None;
        }

        private bool IsAiming()
        {
            return aimState is FPSAimState.Aiming or FPSAimState.PointAiming;
        }

        private void Start()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;

            moveRotation = transform.rotation;

            movementComponent = GetComponent<FPSMovement>();

            movementComponent.onStartMoving.AddListener(OnMoveStarted);
            movementComponent.onStopMoving.AddListener(OnMoveEnded);

            movementComponent.onCrouch.AddListener(OnCrouch);
            movementComponent.onUncrouch.AddListener(OnUncrouch);

            movementComponent.onJump.AddListener(OnJump);
            movementComponent.onLanded.AddListener(OnLand);

            movementComponent.onSprintStarted.AddListener(OnSprintStarted);
            movementComponent.onSprintEnded.AddListener(OnSprintEnded);

            movementComponent.onSlideStarted.AddListener(OnSlideStarted);
            movementComponent.onSlideEnded.AddListener(OnSlideEnded);

            movementComponent.onProneStarted.AddListener(() => collisionLayer.SetLayerAlpha(0f));
            movementComponent.onProneEnded.AddListener(() => collisionLayer.SetLayerAlpha(1f));

            movementComponent.slideCondition += () => !HasActiveAction();
            movementComponent.sprintCondition += () => !HasActiveAction();
            movementComponent.proneCondition += () => !HasActiveAction();

            actionState = FPSActionState.None;

            InitLayers();
            EquipWeapon();
        }

        private void UnequipWeapon()
        {
            DisableAim();

            actionState = FPSActionState.WeaponChange;
            GetAnimGraph().GetFirstPersonAnimator().CrossFade(UnEquip, 0.1f);
        }

        public void ResetActionState()
        {
            actionState = FPSActionState.None;
        }

        public void RefreshStagedState()
        {
        }
        
        public void ResetStagedState()
        {
        }

        private void EquipWeapon()
        {
            if (weapons.Count == 0) return;

            weapons[_lastIndex].gameObject.SetActive(false);
            var gun = weapons[_currentWeaponIndex];

            _bursts = gun.burstAmount;
            
            InitWeapon(gun);
            gun.gameObject.SetActive(true);

            animator.SetFloat(OverlayType, (float) gun.overlayType);
            actionState = FPSActionState.None;
        }

        private void EnableUnarmedState()
        {
            if (weapons.Count == 0) return;
            
            weapons[_currentWeaponIndex].gameObject.SetActive(false);
            animator.SetFloat(OverlayType, 0);
        }

        private void DisableAim()
        {
            if (!GetGun().canAim) return;
            
            aimState = FPSAimState.None;
            OnInputAim(false);
            
            adsLayer.SetAds(false);
            adsLayer.SetPointAim(false);
            swayLayer.SetFreeAimEnable(true);
            swayLayer.SetLayerAlpha(1f);
        }

        public void ToggleAim()
        {
            if (!GetGun().canAim) return;
            
            slotLayer.PlayMotion(aimMotionAsset);
            
            if (!IsAiming())
            {
                aimState = FPSAimState.Aiming;
                OnInputAim(true);
                
                adsLayer.SetAds(true);
                swayLayer.SetFreeAimEnable(false);
                swayLayer.SetLayerAlpha(0.5f);
            }
            else
            {
                DisableAim();
            }

            recoilComponent.isAiming = IsAiming();
        }

        public void ChangeScope()
        {
            InitAimPoint(GetGun());
        }

        private void Fire()
        {
            if (HasActiveAction()) return;
            
            GetGun().OnFire();
            PlayAnimation(GetGun().fireClip);
            
            PlayCameraShake(GetGun().cameraShake);

            if (GetGun().recoilPattern != null)
            {
                float aimRatio = IsAiming() ? GetGun().recoilPattern.aimRatio : 1f;
                float hRecoil = Random.Range(GetGun().recoilPattern.horizontalVariation.x,
                    GetGun().recoilPattern.horizontalVariation.y);
                _controllerRecoil += new Vector2(hRecoil, _recoilStep) * aimRatio;
            }
            
            if (recoilComponent == null || GetGun().weaponAsset.recoilData == null)
            {
                return;
            }
            
            recoilComponent.Play();
            
            if (recoilComponent.fireMode == FireMode.Burst)
            {
                if (_bursts == 0)
                {
                    OnFireReleased();
                    return;
                }
                
                _bursts--;
            }

            if (recoilComponent.fireMode == FireMode.Semi)
            {
                _isFiring = false;
                return;
            }
            
            Invoke(nameof(Fire), 60f / GetGun().fireRate);
            _recoilStep += GetGun().recoilPattern.acceleration;
        }

        private void OnFirePressed()
        {
            if (weapons.Count == 0 || HasActiveAction()) return;

            if (Mathf.Approximately(GetGun().fireRate, 0f))
            {
                return;
            }

            if (Time.unscaledTime - _lastRecoilTime < 60f / GetGun().fireRate)
            {
                return;
            }

            _lastRecoilTime = Time.unscaledTime;
            _bursts = GetGun().burstAmount - 1;

            if (GetGun().recoilPattern != null)
            {
                _recoilStep = GetGun().recoilPattern.step;
            }
            
            _isFiring = true;
            Fire();
        }

        private Weapon GetGun()
        {
            if (weapons.Count == 0) return null;
            
            return weapons[_currentWeaponIndex];
        }

        private void OnFireReleased()
        {
            if (weapons.Count == 0) return;

            if (recoilComponent != null)
            {
                recoilComponent.Stop();
            }

            _recoilStep = 0f;
            _isFiring = false;
            CancelInvoke(nameof(Fire));
        }

        private void OnMoveStarted()
        {
            if (slotLayer != null)
            {
                slotLayer.PlayMotion(onStartStopMoving);
            }

            if (movementComponent.PoseState == FPSPoseState.Prone)
            {
                locoLayer.BlendInIkPose(pronePose);
            }
        }

        private void OnMoveEnded()
        {
            if (slotLayer != null)
            {
                slotLayer.PlayMotion(onStartStopMoving);
            }
            
            if (movementComponent.PoseState == FPSPoseState.Prone)
            {
                locoLayer.BlendOutIkPose();
            }
        }

        private void OnSlideStarted()
        {
            lookLayer.SetLayerAlpha(0.3f);
            GetAnimGraph().GetBaseAnimator().CrossFade("Sliding", 0.1f);
        }

        private void OnSlideEnded()
        {
            lookLayer.SetLayerAlpha(1f);
        }

        private void OnSprintStarted()
        {
            OnFireReleased();
            DisableAim();
            
            lookLayer.SetLayerAlpha(0.5f);

            if (GetGun().overlayType == Runtime.OverlayType.Rifle)
            {
                locoLayer.BlendInIkPose(sprintPose);
            }
            
            aimState = FPSAimState.None;

            if (recoilComponent != null)
            {
                recoilComponent.Stop();
            }
        }

        private void OnSprintEnded()
        {
            lookLayer.SetLayerAlpha(1f);
            adsLayer.SetLayerAlpha(1f);
            locoLayer.BlendOutIkPose();
        }

        private void OnCrouch()
        {
            lookLayer.SetPelvisWeight(0f);
            animator.SetBool(Crouching, true);
            slotLayer.PlayMotion(crouchMotionAsset);
            
            GetAnimGraph().GetFirstPersonAnimator().SetFloat("MovementPlayRate", .7f);
        }

        private void OnUncrouch()
        {
            lookLayer.SetPelvisWeight(1f);
            animator.SetBool(Crouching, false);
            slotLayer.PlayMotion(unCrouchMotionAsset);
            
            GetAnimGraph().GetFirstPersonAnimator().SetFloat("MovementPlayRate", 1f);
        }

        private void OnJump()
        {
            slotLayer.PlayMotion(onJumpMotionAsset);
        }

        private void OnLand()
        {
            slotLayer.PlayMotion(onLandedMotionAsset);
        }

        private void TryReload()
        {
            if (HasActiveAction()) return;

            var reloadClip = GetGun().reloadClip;

            if (reloadClip == null) return;
            
            OnFireReleased();
            
            PlayAnimation(reloadClip);
            GetGun().Reload();
            actionState = FPSActionState.Reloading;
        }

        private void TryGrenadeThrow()
        {
            if (HasActiveAction()) return;
            if (GetGun().grenadeClip == null) return;
            
            OnFireReleased();
            DisableAim();
            PlayAnimation(GetGun().grenadeClip);
            actionState = FPSActionState.Reloading;
        }

        private bool _isLeaning;
        
        private void ChangeWeapon_Internal(int newIndex)
        {
            if (newIndex == _currentWeaponIndex || newIndex > weapons.Count - 1)
            {
                return;
            }
            
            _lastIndex = _currentWeaponIndex;
            _currentWeaponIndex = newIndex;
            
            OnFireReleased();

            UnequipWeapon();
            Invoke(nameof(EquipWeapon), equipDelay);
        }

        private void HandleWeaponChangeInput()
        {
            if (movementComponent.PoseState == FPSPoseState.Prone) return;
            if (HasActiveAction() || weapons.Count == 0) return;
            
            if (Input.GetKeyDown(KeyCode.F))
            {
                ChangeWeapon_Internal(_currentWeaponIndex + 1 > weapons.Count - 1 ? 0 : _currentWeaponIndex + 1);
                return;
            }
            
            for (int i = (int) KeyCode.Alpha1; i <= (int) KeyCode.Alpha9; i++)
            {
                if (Input.GetKeyDown((KeyCode) i))
                {
                    ChangeWeapon_Internal(i - (int) KeyCode.Alpha1);
                }
            }
        }

        private void UpdateActionInput()
        {
            if (movementComponent.MovementState == FPSMovementState.Sprinting)
            {
                return;
            }
            
            if (Input.GetKeyDown(KeyCode.R))
            {
                TryReload();
            }

            if (Input.GetKeyDown(KeyCode.G))
            {
                TryGrenadeThrow();
            }

            HandleWeaponChangeInput();
            
            if (aimState != FPSAimState.Ready)
            {
                bool wasLeaning = _isLeaning;
                
                bool rightLean = Input.GetKey(KeyCode.E);
                bool leftLean = Input.GetKey(KeyCode.Q);

                _isLeaning = rightLean || leftLean;
                
                if (_isLeaning != wasLeaning)
                {
                    slotLayer.PlayMotion(leanMotionAsset);
                    charAnimData.SetLeanInput(wasLeaning ? 0f : rightLean ? -startLean : startLean);
                }

                if (_isLeaning)
                {
                    float leanValue = Input.GetAxis("Mouse ScrollWheel") * smoothLeanStep;
                    charAnimData.AddLeanInput(leanValue);
                }

                if (Input.GetKeyDown(KeyCode.Mouse0))
                {
                    OnFirePressed();
                }

                if (Input.GetKeyUp(KeyCode.Mouse0))
                {
                    OnFireReleased();
                }

                if (Input.GetKeyDown(KeyCode.Mouse1))
                {
                    ToggleAim();
                }

                if (Input.GetKeyDown(KeyCode.V))
                {
                    ChangeScope();
                }

                if (Input.GetKeyDown(KeyCode.B) && IsAiming())
                {
                    if (aimState == FPSAimState.PointAiming)
                    {
                        adsLayer.SetPointAim(false);
                        aimState = FPSAimState.Aiming;
                    }
                    else
                    {
                        adsLayer.SetPointAim(true);
                        aimState = FPSAimState.PointAiming;
                    }
                }
            }
            
            if (Input.GetKeyDown(KeyCode.H))
            {
                if (aimState == FPSAimState.Ready)
                {
                    aimState = FPSAimState.None;
                    lookLayer.SetLayerAlpha(1f);
                }
                else
                {
                    aimState = FPSAimState.Ready;
                    lookLayer.SetLayerAlpha(.5f);
                    OnFireReleased();
                }
            }
        }

        private Quaternion desiredRotation;
        private Quaternion moveRotation;
        private float turnProgress = 1f;
        private bool isTurning = false;

        private void TurnInPlace()
        {
            float turnInput = _playerInput.x;
            _playerInput.x = Mathf.Clamp(_playerInput.x, -90f, 90f);
            turnInput -= _playerInput.x;

            float sign = Mathf.Sign(_playerInput.x);
            if (Mathf.Abs(_playerInput.x) > turnInPlaceAngle)
            {
                if (!isTurning)
                {
                    turnProgress = 0f;
                    
                    animator.ResetTrigger(TurnRight);
                    animator.ResetTrigger(TurnLeft);
                    
                    animator.SetTrigger(sign > 0f ? TurnRight : TurnLeft);
                }
                
                isTurning = true;
            }

            transform.rotation *= Quaternion.Euler(0f, turnInput, 0f);
            
            float lastProgress = turnCurve.Evaluate(turnProgress);
            turnProgress += Time.deltaTime * turnSpeed;
            turnProgress = Mathf.Min(turnProgress, 1f);
            
            float deltaProgress = turnCurve.Evaluate(turnProgress) - lastProgress;

            _playerInput.x -= sign * turnInPlaceAngle * deltaProgress;
            
            transform.rotation *= Quaternion.Slerp(Quaternion.identity,
                Quaternion.Euler(0f, sign * turnInPlaceAngle, 0f), deltaProgress);
            
            if (Mathf.Approximately(turnProgress, 1f) && isTurning)
            {
                isTurning = false;
            }
        }

        private float _jumpState = 0f;

        private void UpdateLookInput()
        {
            _freeLook = Input.GetKey(KeyCode.X);

            float deltaMouseX = Input.GetAxis("Mouse X") * sensitivity;
            float deltaMouseY = -Input.GetAxis("Mouse Y") * sensitivity;
            
            if (_freeLook)
            {
                // No input for both controller and animation component. We only want to rotate the camera

                _freeLookInput.x += deltaMouseX;
                _freeLookInput.y += deltaMouseY;

                _freeLookInput.x = Mathf.Clamp(_freeLookInput.x, -freeLookAngle.x, freeLookAngle.x);
                _freeLookInput.y = Mathf.Clamp(_freeLookInput.y, -freeLookAngle.y, freeLookAngle.y);

                return;
            }

            _freeLookInput = Vector2.Lerp(_freeLookInput, Vector2.zero, 
                FPSAnimLib.ExpDecayAlpha(15f, Time.deltaTime));
            
            _playerInput.x += deltaMouseX;
            _playerInput.y += deltaMouseY;
            
            float proneWeight = animator.GetFloat("ProneWeight");
            Vector2 pitchClamp = Vector2.Lerp(new Vector2(-90f, 90f), new Vector2(-30, 0f), proneWeight);
            
            _playerInput.y = Mathf.Clamp(_playerInput.y, pitchClamp.x, pitchClamp.y);
            moveRotation *= Quaternion.Euler(0f, deltaMouseX, 0f);
            TurnInPlace();

            _jumpState = Mathf.Lerp(_jumpState, movementComponent.IsInAir() ? 1f : 0f,
                FPSAnimLib.ExpDecayAlpha(10f, Time.deltaTime));

            float moveWeight = Mathf.Clamp01(movementComponent.AnimatorVelocity.magnitude);
            transform.rotation = Quaternion.Slerp(transform.rotation, moveRotation, moveWeight);
            transform.rotation = Quaternion.Slerp(transform.rotation, moveRotation, _jumpState);
            _playerInput.x *= 1f - moveWeight;
            _playerInput.x *= 1f - _jumpState;

            charAnimData.SetAimInput(_playerInput);
            charAnimData.AddDeltaInput(new Vector2(deltaMouseX, charAnimData.deltaAimInput.y));
        }

        private Quaternion lastRotation;
        private Vector2 _cameraRecoilOffset;

        private void UpdateRecoil()
        {
            if (Mathf.Approximately(_controllerRecoil.magnitude, 0f)
                && Mathf.Approximately(_cameraRecoilOffset.magnitude, 0f))
            {
                return;
            }
            
            float smoothing = 8f;
            float restoreSpeed = 8f;
            float cameraWeight = 0f;

            if (GetGun().recoilPattern != null)
            {
                smoothing = GetGun().recoilPattern.smoothing;
                restoreSpeed = GetGun().recoilPattern.cameraRestoreSpeed;
                cameraWeight = GetGun().recoilPattern.cameraWeight;
            }
            
            _controllerRecoil = Vector2.Lerp(_controllerRecoil, Vector2.zero,
                FPSAnimLib.ExpDecayAlpha(smoothing, Time.deltaTime));

            _playerInput += _controllerRecoil * Time.deltaTime;
            
            Vector2 clamp = Vector2.Lerp(Vector2.zero, new Vector2(90f, 90f), cameraWeight);
            _cameraRecoilOffset -= _controllerRecoil * Time.deltaTime;
            _cameraRecoilOffset = Vector2.ClampMagnitude(_cameraRecoilOffset, clamp.magnitude);

            if (_isFiring) return;

            _cameraRecoilOffset = Vector2.Lerp(_cameraRecoilOffset, Vector2.zero,
                FPSAnimLib.ExpDecayAlpha(restoreSpeed, Time.deltaTime));
        }

        public void OnWarpStarted()
        {
            movementComponent.enabled = false;
            GetComponent<CharacterController>().enabled = false;
        }

        public void OnWarpEnded()
        {
            movementComponent.enabled = true;
            GetComponent<CharacterController>().enabled = true;
        }

        private void Update()
        {
            Time.timeScale = timeScale;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Application.Quit(0);
            }
            
            UpdateActionInput();
            UpdateLookInput();
            UpdateRecoil();

            charAnimData.moveInput = movementComponent.AnimatorVelocity;
            UpdateAnimController();
        }
        
        public void UpdateCameraRotation()
        {
            Vector2 input = _playerInput;
            input += _cameraRecoilOffset;
            
            (Quaternion, Vector3) cameraTransform =
                (transform.rotation * Quaternion.Euler(input.y, input.x, 0f),
                    firstPersonCamera.position);

            cameraHolder.rotation = cameraTransform.Item1;
            cameraHolder.position = cameraTransform.Item2;

            mainCamera.rotation = cameraHolder.rotation * Quaternion.Euler(_freeLookInput.y, _freeLookInput.x, 0f);
        }
    }
}