﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using Bolt;
using UdpKit;
using UnityEngine;

public abstract class Weapon : MonoBehaviour
{
    public WeaponInfoAsset weaponInfoAsset;
    protected IWeaponOwner weaponOwner;

    public abstract Type InfoAssetType { get; }

    public T GetWeaponInfoAsset<T>() where T : WeaponInfoAsset
    {
        return weaponInfoAsset as T;
    }

    // Start is called before the first frame update
    protected void Start()
    {
    }

    // Update is called once per frame
    protected void Update()
    {
    }

    private void OnValidate()
    {
        if (weaponInfoAsset)
        {
            if (weaponInfoAsset.GetType() != InfoAssetType)
            {
                weaponInfoAsset = null;
                Debug.LogError("Please use a weapon info asset of type: " + InfoAssetType.Name);
            }
        }
    }

    public void AssignWeaponOwner(IWeaponOwner owner)
    {
        weaponOwner = owner;
    }
}

public interface IWeaponOwner
{
    int? playerId { get; }
}