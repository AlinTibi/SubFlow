# SubFlow landing page

`index.html` is a self-contained product page for almarfeld.com: one HTML file with inline CSS, no external scripts, fonts or trackers. The images it needs are in `images/`.

## Publish it

1. Open `index.html` and set the repository URL near the top:
   ```html
   <script>window.SUBFLOW_REPO = "https://github.com/<owner>/<repo>";</script>
   ```
   Every download and GitHub link then points to that repository; downloads go to its latest release. Until it is set, those buttons stay inactive.
2. Upload `index.html` and the `images/` folder to your web server, for example to `https://almarfeld.com/subflow/`.

   You can also publish the folder with GitHub Pages: repository *Settings → Pages → Deploy from a branch*, folder `/website`.

## Updating the screenshots

The screenshots come from the app's QA mode, which renders every page to PNG:

```
SubFlow.exe --data <empty profile folder> --screenshots <output folder> --import <demo videos> --translate ro,fr,es,de
```

Use demo videos on a neutral path, for example a drive letter created with `subst`, so that no personal folder names appear in the pictures. Then copy `dashboard.png`, `queue.png`, `settings.png`, `videos.png` and `translation.png` here.
