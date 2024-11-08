import tkinter as tk
from tkinter import ttk
import json, os, shutil
import threading

class App(tk.Tk):
    destination_folder = ""
    game_name_to_id = {}
    manifests = []
    app_name_to_manifest_path = {}

    def __init__(self):
        super().__init__()

        self.title("My App")
        self.geometry("300x200")

        label = ttk.Label(self, text="Select Game")
        label.pack()

        self.combo = ttk.Combobox(
            self,
            values=self.load_game_entries(),
            state="readonly",
            justify="left",
            width=27,
        )
        self.combo.pack()

        # show selected item belqow the combobox
        self.combo.bind("<<ComboboxSelected>>", self.on_select)

        self.label = ttk.Label(self, text="")
        self.label.pack()

        label = ttk.Label(self, text="Select Folder")
        # folder picker
        self.folder_picker = ttk.Button(
            self, text="Select Destination", command=self.pick_folder
        )
        self.folder_picker.pack()

        # move button
        self.move_button = ttk.Button(
            self, text="Move", command=self.move, state="disabled"
        )
        self.move_button.pack()

        # progress bar
        

    def move(self): # TODO: Handle DLCs
        # move the game
        game = self.combo.get()
        if game == "":
            return

        game_id = self.game_name_to_id[game]
        manifest = self.get_manifest(game_id)
        if manifest == None:
            return

        if len(manifest.get("ExpectingDLCInstalled", {})) > 0:
            from tkinter import messagebox
            messagebox.showerror(
                "Error",
                "This game has DLCs. This tool does not support moving games with DLCs yet.",
            )
            return

        # same install path in multiple manifests
        i = 0
        for m in self.manifests:
            if manifest["InstallLocation"] == m["InstallLocation"]:
                i += 1
            if i > 1:
                from tkinter import messagebox
                messagebox.showerror(
                    "Error",
                    "multiple installation found in the same path. most likely DLCs or UE. This tool does not support moving games with DLCs yet.",
                )
                return
        

        install_location = manifest["InstallLocation"]

        # show confirmation dialog
        from tkinter import messagebox

        result = messagebox.askyesno(
            "Move",
            f'Are you sure you want to move {manifest["DisplayName"]} from "{install_location}" -> "{self.destination_folder}"?',
        )
        if not result:
            return

        self.progress = ttk.Progressbar(self, mode='indeterminate')
        self.progress.pack()
        # start progress bar
        self.progress.start()
        
        def move_game():
            self.combo.config(state="disabled")
            self.folder_picker.config(state="disabled")
            self.move_button.config(state="disabled")
            try:
                shutil.move(install_location, self.destination_folder)
            except Exception as e:
                self.progress.stop()
                messagebox.showerror("Error", f"Failed to move game: {e}")
                return

            try:
                self.update_manifest_install_location(manifest, self.destination_folder)
            except Exception as e:
                self.progress.stop()
                messagebox.showerror("Error", f"Failed to update manifest: {e}")
                messagebox.showinfo("!!", "Trying to revert move")
                # revert the move
                try:
                    shutil.move(self.destination_folder + "\\" + os.path.basename(install_location), os.path.dirname(install_location))
                except Exception as e:
                    messagebox.showerror("Error", f"Failed to revert move: {e}")
                    messagebox.showinfo(
                        "!!",
                        f"You will need to manually move the game back to its original location: {install_location}",
                    )
                return

            self.progress.stop()
            messagebox.showinfo("Success", "Game moved successfully")
            self.quit()

        threading.Thread(target=move_game).start()
    
    def on_select(self, event):
        # get the selected item
        selected_item = self.combo.get()
        self.label.config(text=selected_item)

    def pick_folder(self):
        from tkinter import filedialog

        folder = filedialog.askdirectory()
        import os

        if folder and os.path.isdir(folder):
            self.destination_folder = folder
            self.folder_picker.config(text="Move To: " + folder)

            if self.combo.get() != "":
                self.move_button.config(state="normal")

    def update_manifest_install_location(self, manifest, new_location):
        """
        Update the install location of the manifest and other relevant paths

        Args:
            manifest (dict): The manifest to update
            new_location (str): The new location eg "C:\\Games\\"  shouldn't include the game directory name
        """

        original_install_location = os.path.abspath(manifest["InstallLocation"])
        relative_path = os.path.relpath(
            original_install_location, os.path.dirname(original_install_location)
        )
        new_install_location = os.path.join(new_location, relative_path)
        manifest["InstallLocation"] = new_install_location

        # we don't care because if it's not a subdirectory of the install location, it's not our problem
        if (
            os.path.commonpath(
                [original_install_location, manifest["ManifestLocation"]]
            )
            != original_install_location
        ):
            from tkinter import messagebox  # shouldn't happen

            messagebox.showerror(
                "Info",
                "Manifest location is not a subdirectory of the install location. might cause issue",
            )
        else:
            rel_manifest_location = os.path.relpath(
                manifest["ManifestLocation"], original_install_location
            )
            manifest["ManifestLocation"] = os.path.join(
                new_install_location, rel_manifest_location
            )

        # we don't care because if it's not a subdirectory of the install location, it's not our problem
        if (
            os.path.commonpath([original_install_location, manifest["StagingLocation"]])
            != original_install_location
        ):
            from tkinter import messagebox

            messagebox.showwarning(
                "Info",
                "Staging location is not a subdirectory of the install location.  might cause issue",
            )
        else:
            rel_staging_location = os.path.relpath(
                manifest["StagingLocation"], original_install_location
            )
            manifest["StagingLocation"] = os.path.join(
                new_install_location, rel_staging_location
            )

        # update path in laucher installed file
        launcherInstalledPath = (
            r"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat"
        )

        shutil.copy(launcherInstalledPath, launcherInstalledPath + ".bak")

        with open(launcherInstalledPath, "r") as file:
            data = json.load(file)
            for game in data["InstallationList"]:
                if game["AppName"] == manifest["AppName"]:
                    game["InstallLocation"] = new_install_location
                    break

        with open(launcherInstalledPath, "w") as file:
            json.dump(data, file, indent=4)
    
        # write manifest
        shutil.copy(
            self.app_name_to_manifest_path[manifest["AppName"]],
            self.app_name_to_manifest_path[manifest["AppName"]] + ".bak",
        )
    
        with open(self.app_name_to_manifest_path[manifest["AppName"]], "w") as f:
            json.dump(manifest, f, indent=4)

        return manifest

    def get_manifest(self, game_id):
        if self.manifests == None:
            result = self.load_game_entries()
            if result == None:
                from tkinter import messagebox

                return messagebox.showerror("Error", "Failed to load game entries")

        for manifest in self.manifests:
            if manifest["AppName"] == game_id:
                return manifest
        return None

    def load_game_entries(self):
        # load game entries
        game_entries = []

        # open file
        launcherInstalledPath = (
            r"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat"
        )
        with open(launcherInstalledPath, "r") as file:
            import json

            data = json.load(file)
            for game in data["InstallationList"]:
                game_entries.append(game["AppName"])

        manifests_path = r"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests"

        manifests = []
        import os

        for file in os.listdir(manifests_path):
            if not file.endswith(".item"):
                continue
            with open(os.path.join(manifests_path, file), "r") as f:
                data = json.load(f)
                manifests.append(data)
                self.app_name_to_manifest_path[data["AppName"]] = f.name

        self.manifests = manifests

        game_names = []
        for ge in game_entries:
            for manifest in manifests:
                if manifest["AppName"] == ge:
                    display_name = manifest["DisplayName"]
                    if display_name == ge:
                        game_names.append(display_name)
                    else:
                        game_names.append(display_name + " (" + ge + ")")
                    self.game_name_to_id[game_names[-1]] = manifest["AppName"]
                    break

        return game_names


if __name__ == "__main__":
    app = App()
    app.mainloop()
