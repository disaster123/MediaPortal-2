using MediaPortal.UI.SkinEngine.Controls.ImageSources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MediaPortal.UI.SkinEngine.Rendering;
using MediaPortal.Common.General;
using MediaPortal.Utilities.DeepCopy;
using MediaPortal.Common;
using MediaPortal.Common.Logging;
using MediaPortal.UI.SkinEngine.SkinManagement;
using MediaPortal.UI.SkinEngine.ContentManagement;
using MediaPortal.UI.SkinEngine.Utils;

namespace ImageSourceExtensions
{
  public class ImageSourceWrapper : MultiImageSource
  {
    #region Protected Members
    protected AbstractProperty _sourceProperty;
    protected AbstractProperty _fallbackSourceProperty;
    protected AbstractProperty _delayProperty;
    protected AbstractProperty _delayInOutProperty;
    protected bool _fallbackSourceInUse = false;
    protected bool _needsUpdate = false;
    protected string _currentUri = null;
    protected string _pendingUri = null;
    protected double _pendingDelay = 0;
    protected DateTime _lastChangedTime = DateTime.MinValue;
    #endregion

    #region Ctor
    public ImageSourceWrapper()
    {
      _source = false;
      _sourceProperty = new SProperty(typeof(object), null);
      _fallbackSourceProperty = new SProperty(typeof(object), null);
      _delayProperty = new SProperty(typeof(double), 0d);
      _delayInOutProperty = new SProperty(typeof(bool), false);
      Attach();
    }
    #endregion

    #region Attach / Detach
    protected void Attach()
    {
      _sourceProperty.Attach(OnImageSourceChanged);
      _fallbackSourceProperty.Attach(OnFallbackSourceChanged);
      AttachUriProperties();
    }

    protected void Detach()
    {
      DetachUriProperties();
      _sourceProperty.Detach(OnImageSourceChanged);
      _fallbackSourceProperty.Detach(OnFallbackSourceChanged);
    }

    void AttachUriProperties()
    {
      AttachUriProperty(Source, false);
      AttachUriProperty(FallbackSource, true);
    }

    void DetachUriProperties()
    {
      DetachUriProperty(Source, false);
      DetachUriProperty(FallbackSource, true);
    }

    void AttachUriProperty(object imageSource, bool isFallback)
    {
      AbstractProperty uriProperty;
      if (!TryGetUriProperty(imageSource, out uriProperty))
        return;
      if (isFallback)
        uriProperty.Attach(OnFallbackUriChanged);
      else
        uriProperty.Attach(OnImageSourceUriChanged);
    }

    void DetachUriProperty(object imageSource, bool isFallback)
    {
      AbstractProperty uriProperty;
      if (!TryGetUriProperty(imageSource, out uriProperty))
        return;
      if (isFallback)
        uriProperty.Detach(OnFallbackUriChanged);
      else
        uriProperty.Detach(OnImageSourceUriChanged);
    }
    #endregion

    #region Public Properties
    public AbstractProperty SourceProperty { get { return _sourceProperty; } }
    /// <summary>
    /// The primary ImageSource.
    /// Can either be a path to an image or an ImageSource that has a Uri property (MultiImageSource or BitmapImageSource)
    /// </summary>
    public object Source
    {
      get { return _sourceProperty.GetValue(); }
      set { _sourceProperty.SetValue(value); }
    }

    public AbstractProperty FallbackSourceProperty { get { return _fallbackSourceProperty; } }
    /// <summary>
    /// The ImageSource to use if the primary source cannot be loaded.
    /// Can either be a path to an image or an ImageSource that has a Uri property (MultiImageSource or BitmapImageSource)
    /// </summary>
    public object FallbackSource
    {
      get { return _fallbackSourceProperty.GetValue(); }
      set { _fallbackSourceProperty.SetValue(value); }
    }

    public AbstractProperty DelayProperty { get { return _delayProperty; } }
    /// <summary>
    /// The amount of time in seconds to delay the transition. Useful for avoiding loading images when scrolling quickly.
    /// </summary>
    public double Delay
    {
      get { return (double)_delayProperty.GetValue(); }
      set { _delayProperty.SetValue(value); }
    }

    public AbstractProperty DelayInOutProperty { get { return _delayInOutProperty; } }
    /// <summary>
    /// Gets or sets the a value indicating whether to delay the transition when either the source or target is Null.
    /// </summary>
    public bool DelayInOut
    {
      get { return (bool)_delayInOutProperty.GetValue(); }
      set { _delayInOutProperty.SetValue(value); }
    }

    public override bool IsAllocated
    {
      //Checking whether the trasition is active works around a bug in MultiImageSource when transitioning to null
      get { return base.IsAllocated || TransitionActive; }
    }
    #endregion

    #region Overrides
    public override void DeepCopy(IDeepCopyable source, ICopyManager copyManager)
    {
      Detach();
      base.DeepCopy(source, copyManager);
      ImageSourceWrapper delayedImageSource = (ImageSourceWrapper)source;
      Source = copyManager.GetCopy(delayedImageSource.Source);
      FallbackSource = copyManager.GetCopy(delayedImageSource.FallbackSource);
      Delay = delayedImageSource.Delay;
      DelayInOut = delayedImageSource.DelayInOut;
      _needsUpdate = true;
      Attach();
    }

    public override void Allocate()
    {
      //Check if it's time to switch to next texture
      CheckAndUpdateTexture();
      // Check our previous texture is allocated. Synchronous.
      TextureAsset lastTexture = _lastTexture;
      TextureAsset currentTexture = _currentTexture;
      TextureAsset nextTexture = _nextTexture;
      if (lastTexture != null && !lastTexture.IsAllocated)
        lastTexture.Allocate();
      // Check our current texture is allocated. Synchronous.
      if (currentTexture != null && !currentTexture.IsAllocated)
        currentTexture.Allocate();

      // Check our next texture is allocated. Asynchronous.
      if (nextTexture != null)
      {
        if (!_fallbackSourceInUse && nextTexture.LoadFailed)
        {
          //Load failed, try fallback source
          _fallbackSourceInUse = true;
          string uri = GetUri(FallbackSource);
          if (!string.IsNullOrEmpty(uri))
          {
            nextTexture = ContentManager.Instance.GetTexture(uri, DecodePixelWidth, DecodePixelHeight, Thumbnail);
            nextTexture.ThumbnailDimension = ThumbnailDimension;
            _nextTexture = nextTexture;
          }
        }

        //Check LoadFailed again in case we switched to fallback source
        if (!nextTexture.LoadFailed)
          nextTexture.AllocateAsync();
        else
        {
          _nextTexture = null;
          if (_currentTexture != null)
            CycleTextures(RightAngledRotation.Zero); // If new texture cannot be loaded, we allow switching to "empty" texture
          return;
        }

        if (!_transitionActive && nextTexture.IsAllocated)
          CycleTextures(RightAngledRotation.Zero);
      }
    }

    public override void Dispose()
    {
      DetachUriProperties();
      base.Dispose();
    }
    #endregion

    #region Changed Handlers
    void OnImageSourceChanged(AbstractProperty property, object oldValue)
    {
      DetachUriProperty(oldValue, false);
      AttachUriProperty(Source, false);
      OnImageSourceUriChanged();
    }

    void OnImageSourceUriChanged(AbstractProperty property, object oldValue)
    {
      OnImageSourceUriChanged();
    }

    void OnImageSourceUriChanged()
    {
      string uri = GetUri(Source);
      if (!string.IsNullOrEmpty(uri))
        _fallbackSourceInUse = false;
      else
      {
        if (_fallbackSourceInUse)
          return;
        _fallbackSourceInUse = true;
        uri = GetUri(FallbackSource);
      }
      ScheduleUpdate(uri);
    }

    void OnFallbackSourceChanged(AbstractProperty property, object oldValue)
    {
      DetachUriProperty(oldValue, true);
      AttachUriProperty(FallbackSource, true);
      OnFallbackUriChanged();
    }

    void OnFallbackUriChanged(AbstractProperty property, object oldValue)
    {
      OnFallbackUriChanged();
    }

    void OnFallbackUriChanged()
    {
      if (!_fallbackSourceInUse)
        return;
      string uri = GetUri(FallbackSource);
      ScheduleUpdate(uri);
    }
    #endregion

    #region Image Updating
    string GetUri(object imageSource)
    {
      if (imageSource == null)
        return null;

      string uri = imageSource as string;
      if (uri != null)
        return uri;

      ImageSource convertedSource;
      if (!ImageSourceFactory.TryCreateImageSource(imageSource, 0, 0, out convertedSource))
        return null;

      AbstractProperty uriProperty;
      if (TryGetUriProperty(convertedSource, out uriProperty))
        return uriProperty.GetValue() as string;

      ServiceRegistration.Get<ILogger>().Warn("ImageSourceWrapper: Unsupported ImageSource type '{0}'", imageSource.GetType());
      return null;
    }

    void ScheduleUpdate(string uri)
    {
      bool force = !DelayInOut && (string.IsNullOrEmpty(_pendingUri) || string.IsNullOrEmpty(uri));
      _pendingDelay = force ? 0 : Delay;
      _pendingUri = uri;
      _lastChangedTime = DateTime.Now;
      _needsUpdate = true;
    }

    void CheckAndUpdateTexture()
    {
      if (!_needsUpdate || (SkinContext.FrameRenderingStartTime - _lastChangedTime).TotalSeconds < _pendingDelay)
        return;

      _needsUpdate = false;
      string uri = _pendingUri;
      if (string.IsNullOrEmpty(uri))
      {
        _nextTexture = null;
        if (_currentTexture != null)
          CycleTextures(RightAngledRotation.Zero);
      }
      else
      {
        _currentUri = uri;
        _nextTexture = ContentManager.Instance.GetTexture(uri, DecodePixelWidth, DecodePixelHeight, Thumbnail);
        _nextTexture.ThumbnailDimension = ThumbnailDimension;
      }
    }

    bool TryGetUriProperty(object imageSource, out AbstractProperty uriProperty)
    {
      uriProperty = null;
      if (imageSource == null)
        return false;

      MultiImageSource miSource = imageSource as MultiImageSource;
      if (miSource != null)
      {
        uriProperty = miSource.UriSourceProperty;
        return true;
      }

      BitmapImageSource bSource = imageSource as BitmapImageSource;
      if (bSource != null)
      {
        uriProperty = bSource.UriSourceProperty;
        return true;
      }
      return false;
    }
    #endregion
  }
}