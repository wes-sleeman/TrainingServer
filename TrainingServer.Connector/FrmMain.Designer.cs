﻿namespace TrainingServer.Connector;

partial class FrmMain
{
	/// <summary>
	///  Required designer variable.
	/// </summary>
	private System.ComponentModel.IContainer components = null;

	/// <summary>
	///  Clean up any resources being used.
	/// </summary>
	/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
	protected override void Dispose(bool disposing)
	{
		if (disposing && (components != null))
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

	#region Windows Form Designer generated code

	/// <summary>
	///  Required method for Designer support - do not modify
	///  the contents of this method with the code editor.
	/// </summary>
	private void InitializeComponent()
	{
		LbxServerList = new ListBox();
		SuspendLayout();
		// 
		// LbxServerList
		// 
		LbxServerList.Dock = DockStyle.Fill;
		LbxServerList.FormattingEnabled = true;
		LbxServerList.ItemHeight = 15;
		LbxServerList.Location = new Point(0, 0);
		LbxServerList.Name = "LbxServerList";
		LbxServerList.Size = new Size(800, 450);
		LbxServerList.TabIndex = 1;
		LbxServerList.SelectedIndexChanged += LbxServerList_SelectedIndexChanged;
		// 
		// FrmMain
		// 
		AutoScaleDimensions = new SizeF(7F, 15F);
		AutoScaleMode = AutoScaleMode.Font;
		ClientSize = new Size(800, 450);
		Controls.Add(LbxServerList);
		Name = "FrmMain";
		Text = "Training System FSD Connector";
		ResumeLayout(false);
	}

	#endregion

	private ListBox LbxServerList;
}
