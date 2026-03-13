namespace Bivium.Models
{
    /// <summary>
    /// Available fields for sorting
    /// </summary>
    public enum SortField
    {
        /// <summary>
        /// Sort by name
        /// </summary>
        Name,

        /// <summary>
        /// Sort by size
        /// </summary>
        Size,

        /// <summary>
        /// Sort by last modified date
        /// </summary>
        Date,

        /// <summary>
        /// Sort by attributes/permissions
        /// </summary>
        Attributes,

        /// <summary>
        /// Sort by owner
        /// </summary>
        Owner
    }

    /// <summary>
    /// Sort direction
    /// </summary>
    public enum SortDirection
    {
        /// <summary>
        /// Ascending order
        /// </summary>
        Ascending,

        /// <summary>
        /// Descending order
        /// </summary>
        Descending
    }

    /// <summary>
    /// Represents a sort column with field and direction
    /// </summary>
    public class SortColumn
    {
        #region Properties

        /// <summary>
        /// Field to sort by
        /// </summary>
        public SortField Field { get; set; } = SortField.Name;

        /// <summary>
        /// Sort direction
        /// </summary>
        public SortDirection Direction { get; set; } = SortDirection.Ascending;

        #endregion

        #region Constructor

        /// <summary>
        /// Default constructor - sort by Name ascending
        /// </summary>
        public SortColumn()
        {
        }

        /// <summary>
        /// Creates a SortColumn with specified field and direction
        /// </summary>
        /// <param name="field">Sort field</param>
        /// <param name="direction">Sort direction</param>
        public SortColumn(SortField field, SortDirection direction)
        {
            this.Field = field;
            this.Direction = direction;
        }

        #endregion
    }
}
