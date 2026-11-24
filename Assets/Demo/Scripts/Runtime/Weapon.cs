// Designed by Kinemation, 2023

using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;

using System.Collections.Generic;
using UnityEngine;

namespace Demo.Scripts.Runtime
{
    public enum OverlayType
    {
        Default,
        Pistol,
        Rifle
    }
    
    public class Weapon : FPSAnimatorEntity
    {
        [Header("Animations")]
        public FPSAnimationAsset overlayPose;
        public FPSAnimationAsset reloadClip;
        public FPSAnimationAsset grenadeClip;
        public FPSAnimationAsset fireClip;
        public OverlayType overlayType;
        
        [Header("Aiming")]
        public bool canAim = true;
        [SerializeField] private List<Transform> scopes;
        [SerializeField] private Transform pointAim;
        
        [Header("Recoil")]
        public RecoilPattern recoilPattern;
        public FPSCameraShake cameraShake;
        public RecoilAnimData recoilData;
        public float fireRate;
        public FireMode fireMode;
        public int burstAmount;
        
        private Animator _animator;
        private int _scopeIndex;

        protected void Start()
        {
            _animator = GetComponentInChildren<Animator>();
        }

        public void UpdateAimPoint(bool cycleScope, bool pointAiming)
        {
            if (scopes != null && scopes.Count > 0)
            {
                if (cycleScope) _scopeIndex = (_scopeIndex + 1) % scopes.Count;
                defaultAimPoint = pointAiming && pointAim != null ? pointAim : scopes[_scopeIndex];
            }

            if (defaultAimPoint == null) defaultAimPoint = transform;
        }
        
        public void OnFire()
        {
            if (_animator == null)
            {
                return;
            }
            
            _animator.Play("Fire", 0, 0f);
        }

        public void Reload()
        {
            if (_animator == null)
            {
                return;
            }
            
            _animator.Rebind();
            _animator.Play("Reload", 0);
        }
    }
}
