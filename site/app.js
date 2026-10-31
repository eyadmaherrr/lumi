// Cycles the hero notch through Lumi's states, like the real island morphing.
(function () {
  var notch = document.getElementById("notch");
  var caption = document.getElementById("caption");
  var focusTime = document.getElementById("focusTime");
  if (!notch) return;

  var states = [
    { name: "music", label: "Now playing", hold: 2600 },
    { name: "airpods", label: "AirPods connected", hold: 2600 },
    { name: "focus", label: "Focus session", hold: 2600 },
    { name: "player", label: "Hover to open", hold: 3600 }
  ];
  var index = 0;
  var seconds = 17 * 60 + 40;

  function show(i) {
    var state = states[i];
    notch.className = "notch state-" + state.name;
    caption.style.opacity = 0;
    setTimeout(function () {
      caption.textContent = state.label;
      caption.style.opacity = 1;
    }, 250);
    setTimeout(function () {
      index = (index + 1) % states.length;
      show(index);
    }, state.hold);
  }

  setInterval(function () {
    seconds = Math.max(0, seconds - 1);
    focusTime.textContent = Math.floor(seconds / 60) + ":" + String(seconds % 60).padStart(2, "0");
  }, 1000);

  if (window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    notch.className = "notch state-player";
    caption.textContent = "Hover to open";
    return;
  }
  show(0);
})();
