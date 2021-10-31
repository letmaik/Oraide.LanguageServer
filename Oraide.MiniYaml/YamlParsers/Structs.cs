﻿using System.Collections.Generic;

namespace Oraide.MiniYaml.YamlParsers
{
	public class MyModData
	{
		public MyLoadScreen LoadScreen { get; set; }

		public List<string> Packages { get; set; }
	}

	public class MyLoadScreen
	{
		public string Image { get; set; }

		public string Image2x { get; set; }

		public string Image3x { get; set; }

		public string Text { get; set; }

		public string Type { get; set; }
	}
}
