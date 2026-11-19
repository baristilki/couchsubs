// SPDX-License-Identifier: AGPL-3.0-only
// Tizen'e (Samsung TV) özgü işler. webOS karşılığı: tv-webos/platform.js
// HDMI görüntüsü resmî TVWindow API'siyle HTML katmanının arkasında gösterilir.
(function () {
  'use strict';

  var KEYS = ['0', '1', '2', '3', '4', 'ColorF0Red', 'ColorF1Green', 'ColorF3Blue'];
  // Çözünürlük değişikliği dinleyicisi (bir kez kurulur) ve güncel değeri alan işlev.
  var resolutionListener = null;
  var resolutionCallback = null;
  // HDMI görüntüsü uygulama yüzeyinin tamamını kaplar (yüzde, ekran kökenli; Samsung TVWindow API).
  var FULL_SCREEN = ['0', '0', '100%', '100%'];
  // 16:9'dan belirgin geniş yüzey (21:9 = 2,33; 3440x1440 = 2,39; 32:9 = 3,56). 16:9 (1,78) ve yuvarlama payı altında.
  var WIDE_RATIO = 1.9;
  // Sayfa her TV'de 1080 CSS piksel yükseklikte düzenlenir (index.html: width=1920); geniş ekranda da öyle kalır.
  var PAGE_HEIGHT = 1080;

  // Ultrawide ekran (#304): config.xml'deki base_screen_resolution=fullscreen ile yüzey ekranın tamamı olur. Ortak
  // index.html'in sabit 1920 piksellik görünümü bu yüzeyde sayfayı 1920x800'e indirir, yazılar 16:9 TV'dekinden küçük
  // kalırdı. Geniş yüzeyde görünüm genişliği yüksekliği 1080 tutacak şekilde verilir (3440x1440 için 2580): yazı ve
  // kutular 16:9 TV'dekiyle aynı boyda, app.js yerleşimi innerWidth/innerHeight'tan hesaplandığı için ekrana yayılır.
  // 16:9 yüzeyde (bütün TV'ler, ayarı tanımayan modeller) null döner: hiçbir şey değişmez.
  function viewportFor(width, height) {
    if (!(width > 0) || !(height > 0)) return null;
    var ratio = width / height;
    return ratio >= WIDE_RATIO ? 'width=' + Math.round(PAGE_HEIGHT * ratio) : null;
  }

  function fitWideScreen() {
    try {
      var content = viewportFor(window.innerWidth, window.innerHeight);
      if (!content) return;
      var meta = document.querySelector('meta[name="viewport"]');
      if (meta) meta.setAttribute('content', content);
    } catch (e) {}
  }

  function hdmiSources(callback) {
    try {
      tizen.systeminfo.getPropertyValue('VIDEOSOURCE', function (info) {
        var isHdmi = function (source) { return source.type === 'HDMI'; };
        callback((info.connected || []).filter(isHdmi), (info.disconnected || []).filter(isHdmi));
      }, function () { callback([], []); });
    } catch (e) {
      callback([], []);
    }
  }

  // CouchSubs uygulaması TV uygulamasını açarken parametreleri PAYLOAD içinde verir.
  function paramsOf(appControl) {
    var data = (appControl && appControl.data) || [];
    for (var i = 0; i < data.length; i++) {
      if (data[i].key !== 'PAYLOAD' || !data[i].value || !data[i].value.length) continue;
      try {
        var payload = JSON.parse(data[i].value[0]);
        return payload && payload.values !== undefined ? payload.values : payload;
      } catch (e) {
        return null;
      }
    }
    return null;
  }

  function requestedParams() {
    try {
      return paramsOf(tizen.application.getCurrentApplication().getRequestedAppControl().appControl);
    } catch (e) {
      return null;
    }
  }

  // app.js yerleşimi ölçmeden önce; platform.js ondan önce yüklenir.
  fitWideScreen();

  window.COUCHSUBS_PLATFORM = {
    backKey: 10009,

    // Birim testi için (scripts/tv-tizen.test.js).
    viewportFor: viewportFor,

    // Anynet+ (HDMI-CEC) ya da Otomatik Kaynak Değiştirme TV'yi kendiliğinden HDMI'ya geçirirse uygulama ipucu
    // gösterir (app.js). Menü adları Samsung'unkiler; başka TV'lerde gösterilmez.
    sourceSwitchTip: true,
    // HDMI girişi açılamazsa app.js nedenini ve giriş seçimini gösterir.
    noPictureHelp: true,
    // Arka plana düşen uygulama bağlantıyı kapatır: Samsung önplandaki uygulamayı söylemediği için bilgisayar
    // uygulamayı ancak bağlantı yoksa yeniden açar (TvKeeper).
    closeWhenHidden: true,

    init: function () {
      // Renk ve sayı tuşları istenmezse TV kendisi kullanır.
      for (var i = 0; i < KEYS.length; i++) {
        try { tizen.tvinputdevice.registerKey(KEYS[i]); } catch (e) {}
      }
      // Oyun TV kumandasına dokunmadan oynanır; TV boşta sanıp ekran koruyucuyu açmasın (Samsung AppCommon API).
      // Eski TV'lerde API yoksa sessizce geçilir.
      try {
        webapis.appcommon.setScreenSaver(webapis.appcommon.AppCommonScreenSaverState.SCREEN_SAVER_OFF);
      } catch (e) {}
      // Ekranın kararmasını ve TV'nin boşta sanıp kapanmasını Tizen'in güç API'siyle de engeller (izin: power).
      // Uygulama kapanınca istek kendiliğinden düşer. API ya da izin yoksa sessizce geçilir.
      try {
        tizen.power.request('SCREEN', 'SCREEN_NORMAL');
      } catch (e) {}
    },

    launchParams: requestedParams,

    onRelaunch: function (callback) {
      window.addEventListener('appcontrol', function () {
        var params = requestedParams();
        if (params) callback(params);
      });
    },

    // onStatus: 'loading' | 'ready' | 'playing' | 'inputError' | 'sourceError'
    showHdmi: function (port, host, onStatus) {
      onStatus('loading');
      hdmiSources(function (connected, disconnected) {
        var all = connected.concat(disconnected);
        var source = all.filter(function (s) { return s.number === port; })[0];
        if (!source) return onStatus('sourceError');
        var failed = function () { onStatus('inputError'); };
        try {
          tizen.tvwindow.setSource(source, function () {
            tizen.tvwindow.show(function () { onStatus('playing'); }, failed,
              FULL_SCREEN, 'MAIN', 'BEHIND');
          }, failed);
        } catch (e) {
          failed();
        }
      });
    },

    // TV'nin ağ adresi; bilgisayarı aramak için.
    localAddress: function (callback) {
      function read(property, otherwise) {
        try {
          tizen.systeminfo.getPropertyValue(property, function (info) {
            if (info && info.ipAddress) callback(info.ipAddress); else otherwise();
          }, otherwise);
        } catch (e) {
          otherwise();
        }
      }
      read('ETHERNET_NETWORK', function () {
        read('WIFI_NETWORK', function () { callback(null); });
      });
    },

    // Tek bağlı HDMI varsa odur; birden çoksa TV'nin o anki girişi tercih edilir. Tizen girişteki cihazın adını
    // vermez, bu yüzden birden çok bağlı girişte seçim tahmindir: üçüncü değer bağlı HDMI sayısıdır, app.js bu
    // durumda kullanıcıya girişi sorar. Bilgisayar da TV'ye HDMI ile bağlıyken TV'nin o anki girişi bilgisayarınki
    // oluyordu; TV uygulaması Xbox yerine bilgisayarın ekranını gösteriyor, altyazı oyunun üstüne çıkmıyordu (#360).
    findConsolePort: function (callback) {
      hdmiSources(function (connected) {
        var pick = connected.length === 1 ? connected[0] : null;
        if (!pick && connected.length > 1) {
          try {
            var current = tizen.tvwindow.getSource();
            pick = connected.filter(function (s) { return s.number === current.number && current.type === 'HDMI'; })[0] || null;
          } catch (e) {}
        }
        callback(pick ? pick.number : null, pick ? 'HDMI ' + pick.number : null, connected.length);
      });
    },

    // HDMI görüntüsünün çözünürlüğü ve yenileme hızı (TVWindow API: getVideoResolution). İlk değer hemen, sonrakiler
    // değiştikçe callback'e gelir; dinleyici bir kez kurulur. API yoksa ya da hata verirse callback(null): gösterilmez.
    videoResolution: function (callback) {
      resolutionCallback = callback;
      var report = function (info) {
        if (resolutionCallback) resolutionCallback(info || null);
      };
      var fail = function () { report(null); };
      try {
        var result = tizen.tvwindow.getVideoResolution(report, fail, 'MAIN');
        // Eski sürümler sonucu doğrudan döndürür.
        if (result && result.width) report(result);
      } catch (e) {
        fail();
      }
      if (resolutionListener === null) {
        try {
          resolutionListener = tizen.tvwindow.addVideoResolutionChangeListener(report, 'MAIN');
        } catch (e) {
          resolutionListener = false;
        }
      }
    },

    exit: function () {
      try { tizen.application.getCurrentApplication().exit(); } catch (e) {}
    }
  };
})();
