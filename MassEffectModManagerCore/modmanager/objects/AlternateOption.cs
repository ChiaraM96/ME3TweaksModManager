﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

namespace MassEffectModManagerCore.modmanager.objects
{
    public abstract class AlternateOption : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public virtual bool CheckedByDefault { get; internal set; }
        public abstract bool IsManual { get; }
        public virtual double CheckboxOpacity => (!UIIsSelectable) ? .5 : 1;
        public virtual double TextOpacity
        {
            get
            {
                if (!UIIsSelectable && IsSelected) return 1;
                if (!UIIsSelectable && !IsSelected) return .5;
                return 1;
            }
        }

        public bool IsSelected { get; set; }
        public virtual bool UIRequired => !IsManual && !IsAlways && IsSelected;
        public abstract bool UINotApplicable { get; }
        public abstract bool UIIsSelectable { get; set; }
        public abstract bool IsAlways { get; }
        public virtual string GroupName { get; internal set; }
        public virtual string FriendlyName { get; internal set; }
        public virtual string Description { get; internal set; }
    }
}
